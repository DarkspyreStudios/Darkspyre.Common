using System.Collections.Frozen;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Owns authenticated connections, admission and request dispatch. The backend observes Draining,
/// closes domain resources and retains native ownership until cleanup completes.
/// </summary>
public sealed partial class ServiceControlHost : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Guid epoch;
    private readonly SubscriberRegistry subscribers;
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<SubscriberUpdate> draining = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task timer;
    private Task? disposal;

    /// <summary>Creates the service control loop with its admitted operations and request handler.</summary>
    public ServiceControlHost(Guid epoch, IReadOnlySet<string> operations, ServiceRequestHandler requestHandler, string stagingPath, TimeProvider? timeProvider = null, long? startupTimestamp = null)
    {
        if (epoch == Guid.Empty) throw new ArgumentException("A service epoch is required.", nameof(epoch));
        this.operations = operations.ToFrozenSet(StringComparer.Ordinal);
        this.requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        this.stagingPath = stagingPath;
        this.epoch = epoch;
        this.subscribers = new(timeProvider, startupTimestamp);
        this.Operations = new(epoch, this.gate, timeProvider, () => Apply(this.subscribers.Tick()));
        this.timer = MonitorAsync(timeProvider ?? TimeProvider.System);
    }

    /// <summary>Trusted backend admission state. Wire business dispatch and authority registration remain separate.</summary>
    public ServiceOperationRegistry Operations { get; }

    /// <summary>Completes when subscriber accounting enters drain.</summary>
    public Task<SubscriberUpdate> Draining => this.draining.Task;

    /// <summary>Takes ownership of the peer until EOF, unsubscribe, expiry, rejection or host disposal.</summary>
    public async Task RunPeerAsync(ServicePeer peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var connection = new Connection(peer, cancellationToken);
        bool registered = false;
        try
        {
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposal is not null, this);
                if (peer.Welcome.Epoch != this.epoch) throw new ServiceProtocolException(ServiceErrorCode.WrongEpoch);
                if (!this.connections.TryAdd(peer.Welcome.ConnectionId, connection))
                    throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
                registered = true;
            }
            using (var first = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token))
            {
                first.CancelAfter(ServiceProtocol.FirstSubscriberDeadline);
                ServiceMessage? message = await ServiceFrameCodec.ReadAsync(peer.Stream, first.Token).ConfigureAwait(false);
                if (message is not SubscribeMessage subscribe) throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
                CheckEpoch(subscribe.Epoch);
                SubscriberUpdate update = Subscribe(peer.Welcome.ConnectionId);
                if (!update.Succeeded) throw new ServiceProtocolException(ServiceErrorCode.ServiceDraining);
                connection.Subscribed = true;
                await connection.SendAsync(new SubscribeAckMessage(this.epoch, peer.Welcome.ConnectionId), first.Token).ConfigureAwait(false);
            }
            while (true)
            {
                ServiceMessage? message = await ServiceFrameCodec.ReadAsync(peer.Stream, connection.Stop.Token).ConfigureAwait(false);
                switch (message)
                {
                    case null: return;
                    case UnsubscribeMessage unsubscribe:
                        CheckEpoch(unsubscribe.Epoch);
                        return;
                    case PingMessage ping:
                        CheckEpoch(ping.Epoch);
                        SubscriberUpdate update = Change(() => this.subscribers.Heartbeat(peer.Welcome.ConnectionId));
                        if (!update.Succeeded) throw new ServiceProtocolException(ServiceErrorCode.CapabilityRevoked);
                        using (var send = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop.Token))
                        {
                            send.CancelAfter(ServiceProtocol.HeartbeatInterval);
                            await connection.SendAsync(new PongMessage(this.epoch), send.Token).ConfigureAwait(false);
                        }
                        break;
                    case RequestMessage request:
                        await DispatchAsync(connection, peer.Welcome.ConnectionId, request).ConfigureAwait(false);
                        break;
                    case BlobChunkMessage chunk:
                        await ReceiveBodyAsync(connection, chunk).ConfigureAwait(false);
                        break;
                    case CancelMessage cancel:
                        CheckEpoch(cancel.Epoch);
                        this.Operations.Cancel(peer.Welcome.ConnectionId, cancel.RequestId);
                        break;
                    case CapabilityResultMessage callback:
                        ReceiveCallbackResult(connection, callback);
                        break;
                    default: throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
                }
            }
        }
        catch (ServiceProtocolException error) { connection.Rejection = error.Code; }
        // A failed frame operation may leave a partial frame. Close it without another protocol write.
        catch (InvalidDataException) { connection.BreakTransport(); }
        catch (OperationCanceledException) { connection.BreakTransport(); }
        catch (IOException) { connection.BreakTransport(); }
        catch (ObjectDisposedException) when (connection.Stop.IsCancellationRequested) { }
        finally
        {
            lock (this.gate)
            {
                connection.Finalizing = true;
                if (registered) Apply(this.subscribers.Unsubscribe(peer.Welcome.ConnectionId));
                foreach (TaskCompletionSource<ServiceCallbackResult> answer in connection.Callbacks.Values) answer.TrySetResult(Unavailable());
            }
            try
            {
                if (connection.Termination is { } termination) await termination.ConfigureAwait(false);
                else if (connection.Rejection is { } rejection)
                    await connection.NotifyAndCloseAsync(rejection).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    connection.BreakTransport();
                    Task[] work;
                    lock (this.gate) work = [.. connection.Work];
                    await Task.WhenAll(work).ConfigureAwait(false);
                    await peer.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (this.gate)
                    {
                        if (registered) this.connections.Remove(peer.Welcome.ConnectionId);
                        connection.Dispose();
                        connection.Completed.TrySetResult();
                    }
                }
            }
        }
    }

    /// <summary>Expires leases independently of receive/business work. Removed identities cannot be readmitted.</summary>
    public SubscriberUpdate Tick() => Change(this.subscribers.Tick);

    /// <summary>Closes admission and publishes the drain transition.</summary>
    public SubscriberUpdate BeginDrain() => Change(this.subscribers.BeginDrain);

    private SubscriberUpdate Subscribe(Guid connectionId)
    {
        lock (this.gate)
        {
            SubscriberUpdate update = this.subscribers.Subscribe(connectionId);
            Apply(update);
            if (update.Succeeded)
            {
                this.Operations.OpenConnection(connectionId);
                this.Operations.RegisterContext(connectionId, 1, this.operations);
            }
            this.Operations.Tick();
            return update;
        }
    }

    private SubscriberUpdate Change(Func<SubscriberUpdate> operation)
    {
        lock (this.gate)
        {
            SubscriberUpdate update = operation();
            Apply(update);
            this.Operations.Tick();
            return update;
        }
    }

    private void Apply(SubscriberUpdate update)
    {
        foreach (Guid id in update.RemovedSubscribers)
        {
            this.Operations.CloseConnection(id, draining: update.State == ServiceLifecycleState.Draining);
            if (this.connections.TryGetValue(id, out Connection? connection))
                connection.Cancel(update.State == ServiceLifecycleState.Draining ? ServiceErrorCode.ServiceDraining : ServiceErrorCode.CapabilityRevoked);
        }
        if (update.State == ServiceLifecycleState.Draining) this.draining.TrySetResult(update);
    }

    private async Task MonitorAsync(TimeProvider time)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try { while (await timer.WaitForNextTickAsync(this.stopping.Token).ConfigureAwait(false)) Tick(); }
        catch (OperationCanceledException) when (this.stopping.IsCancellationRequested) { }
    }

    private void CheckEpoch(Guid value)
    {
        if (value != this.epoch) throw new ServiceProtocolException(ServiceErrorCode.WrongEpoch);
    }

    /// <summary>Joins control-loop cleanup and releases its owned resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (this.gate) return new(this.disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        // The caller holds gate until this method's first suspension. It never joins work under that lock.
        this.stopping.Cancel();
        Apply(this.subscribers.BeginDrain());
        Task[] peers = [.. this.connections.Values.Select(connection => connection.Completed.Task)];
        foreach (Connection connection in this.connections.Values.ToArray()) connection.Cancel(ServiceErrorCode.ServiceDraining);
        await this.timer.ConfigureAwait(false);
        await Task.WhenAll(peers).ConfigureAwait(false);
        this.stopping.Dispose();
    }

    private sealed class Connection(ServicePeer peer, CancellationToken token) : IDisposable
    {
        private readonly SemaphoreSlim writes = new(1);
        private int broken;
        public bool Subscribed { get; set; }
        public Dictionary<Guid, IncomingRequest> Requests { get; } = [];
        public Dictionary<Guid, TaskCompletionSource<ServiceCallbackResult>> Callbacks { get; } = [];
        public HashSet<Task> Work { get; } = [];
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ServiceErrorCode? Rejection { get; set; }
        public bool Finalizing { get; set; }
        public Task? Termination { get; private set; }

        public void Cancel(ServiceErrorCode reason)
        {
            if (!this.Finalizing && this.Termination is null) this.Termination = Task.Run(() => NotifyAndCloseAsync(reason));
        }

        public async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken)
        {
            await this.writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref this.broken) != 0) throw new IOException("The control transport is closed.");
                await ServiceFrameCodec.WriteAsync(peer.Stream, message, cancellationToken).ConfigureAwait(false);
            }
            catch { BreakTransport(); throw; }
            finally { this.writes.Release(); }
        }

        public async Task NotifyAndCloseAsync(ServiceErrorCode reason)
        {
            try
            {
                using var deadline = new CancellationTokenSource(ServiceProtocol.HeartbeatInterval);
                ServiceMessage response = reason == ServiceErrorCode.ServiceDraining
                    ? new DrainingMessage(peer.Welcome.Epoch) : new ProtocolErrorMessage(new(reason, Guid.NewGuid()));
                await SendAsync(response, deadline.Token).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally { BreakTransport(); }
        }

        public void Dispose()
        {
            this.Stop.Dispose();
            this.writes.Dispose();
        }

        public void BreakTransport()
        {
            if (Interlocked.Exchange(ref this.broken, 1) != 0) return;
            // Only frame operations register on this internal token. No business/user callback runs here.
            this.Stop.Cancel();
            peer.Stream.Dispose();
        }
    }
}
