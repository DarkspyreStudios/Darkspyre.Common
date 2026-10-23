using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// An authenticated subscription with heartbeats, requests and invalidations. It owns its connection, never the backend
/// process. One call's cancellation, deadline or wait for the write path ends only that call; the subscription closes only
/// when its connection fails, the service ends it or the subscriber disposes it, and <see cref="CloseReason"/> says which.
/// A closed subscription stays closed: it never reconnects or replays a request. Its owner opens a new one.
/// </summary>
public sealed partial class ServiceSubscription : IServiceSubscription
{
    private readonly ServicePeer peer;
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<ServiceErrorCode?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Task receiver;
    private readonly Task heartbeat;
    private Task? disposal;
    private string? closeReason;

    private ServiceSubscription(ServicePeer peer)
    {
        this.peer = peer;
        this.receiver = ReceiveAsync();
        this.heartbeat = HeartbeatAsync();
    }

    /// <summary>The authenticated service and connection identity.</summary>
    public WelcomeMessage Identity => this.peer.Welcome;
    /// <summary>Completes when the subscription closes, with a safe failure code when present.</summary>
    public Task<ServiceErrorCode?> Closed => this.closed.Task;
    /// <summary>Why the subscription closed, once <see cref="Closed"/> completes; null while it is open.</summary>
    public string? CloseReason { get { lock (this.gate) return this.closeReason; } }

    /// <summary>Takes ownership of the peer. A valid SubscribeAck is required before returning a subscription.</summary>
    public static async Task<ServiceSubscription> SubscribeAsync(ServicePeer peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ServiceProtocol.FirstSubscriberDeadline);
            await ServiceFrameCodec.WriteAsync(peer.Stream, new SubscribeMessage(peer.Welcome.Epoch), deadline.Token).ConfigureAwait(false);
            ServiceMessage? response = await ServiceFrameCodec.ReadAsync(peer.Stream, deadline.Token).ConfigureAwait(false);
            if (response is null) throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
            if (response is ProtocolErrorMessage error) throw new ServiceProtocolException(error.Error.Code);
            if (response is DrainingMessage draining)
                throw new ServiceProtocolException(draining.Epoch == peer.Welcome.Epoch ? ServiceErrorCode.ServiceDraining : ServiceErrorCode.WrongEpoch);
            if (response is not SubscribeAckMessage ack || ack.Epoch != peer.Welcome.Epoch || ack.ConnectionId != peer.Welcome.ConnectionId)
                throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
            return new(peer);
        }
        catch { await peer.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!this.stopping.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token);
                deadline.CancelAfter(ServiceProtocol.SubscriberExpiry);
                ServiceMessage? message = await ServiceFrameCodec.ReadAsync(this.peer.Stream, deadline.Token).ConfigureAwait(false);
                switch (message)
                {
                    case PongMessage pong when pong.Epoch == this.Identity.Epoch: break;
                    case DrainingMessage draining when draining.Epoch == this.Identity.Epoch:
                        Finish(ServiceErrorCode.ServiceDraining, "the service is draining");
                        return;
                    case ProtocolErrorMessage error:
                        Finish(error.Error.Code, $"the service ended the subscription with {error.Error.Code}");
                        return;
                    case AcceptedMessage or ResultMessage or BlobChunkMessage or EventMessage:
                        await ReceiveBusinessAsync(message).ConfigureAwait(false);
                        break;
                    case CapabilityRequestMessage callback:
                        StartCallback(callback);
                        break;
                    case CancelMessage cancel:
                        CancelCallback(cancel);
                        break;
                    case null:
                        Finish(ServiceErrorCode.Unavailable, "the service closed the connection");
                        return;
                    default:
                        Finish(message is PongMessage or DrainingMessage ? ServiceErrorCode.WrongEpoch : ServiceErrorCode.InvalidState,
                            $"the service sent an unexpected {message.GetType().Name}");
                        return;
                }
            }
        }
        catch (ServiceProtocolException error) { Finish(error.Code, $"a frame from the service was refused: {error.Code}"); }
        catch (InvalidDataException error) { Finish(ServiceErrorCode.InvalidFrame, $"a frame from the service could not be read: {error.Message}"); }
        catch (IOException error) { Finish(ServiceErrorCode.Unavailable, $"reading from the service failed: {Describe(error)}"); }
        catch (OperationCanceledException)
        {
            Finish(ServiceErrorCode.Unavailable, $"no frame arrived from the service for {ServiceProtocol.SubscriberExpiry.TotalSeconds:0} s");
        }
        catch (ObjectDisposedException) when (this.stopping.IsCancellationRequested) { }
        catch (Exception error) { Finish(ServiceErrorCode.Unavailable, $"the receive loop failed: {Describe(error)}"); throw; }
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(ServiceProtocol.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(this.stopping.Token).ConfigureAwait(false))
                await SendAsync(new PingMessage(this.Identity.Epoch), this.stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            Finish(ServiceErrorCode.Unavailable, $"a heartbeat could not be sent: {Describe(error)}");
        }
        catch (ObjectDisposedException) when (this.stopping.IsCancellationRequested) { }
    }

    // The wait for the write path has no deadline of its own. The frame ahead finishes or fails within its own deadline, and
    // a failed frame closes the subscription, which ends the wait. Only a frame that takes longer than its deadline to write,
    // or that fails, closes the subscription: it leaves a partial frame on the stream.
    private async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken)
    {
        await this.writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ServiceProtocol.HeartbeatInterval);
            try { await ServiceFrameCodec.WriteAsync(this.peer.Stream, message, deadline.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                Finish(ServiceErrorCode.Unavailable, error is OperationCanceledException && !cancellationToken.IsCancellationRequested
                    ? $"writing a {message.GetType().Name} took longer than {ServiceProtocol.HeartbeatInterval.TotalSeconds:0} s"
                    : $"writing a {message.GetType().Name} failed: {Describe(error)}");
                throw;
            }
        }
        finally { this.writes.Release(); }
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    // The first reason wins; a later failure of the same close is its consequence.
    private void Finish(ServiceErrorCode? code, string reason)
    {
        lock (this.gate)
        {
            if (this.closed.Task.IsCompleted) return;
            this.closeReason = reason;
            this.closed.SetResult(code);
            foreach (PendingCall call in this.pending.Values)
            {
                call.Accepted.TrySetResult(false);
                call.Result.TrySetResult(UnknownResult());
            }
        }
        this.stopping.Cancel();
        this.peer.Stream.Dispose();
    }

    /// <summary>Unsubscribes and joins the receive and heartbeat loops.</summary>
    public ValueTask DisposeAsync()
    {
        lock (this.gate) return new(this.disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (!this.closed.Task.IsCompleted)
                await SendAsync(new UnsubscribeMessage(this.Identity.Epoch), this.stopping.Token).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Finish(null, "the subscriber closed it");
            await Task.WhenAll(this.receiver, this.heartbeat).ConfigureAwait(false);
            Task[] calls;
            lock (this.gate) calls = [.. this.pending.Values.Select(call => call.Finished.Task), .. this.callbacks.Values.Select(callback => callback.Completion)];
            await Task.WhenAll(calls).ConfigureAwait(false);
            await this.peer.DisposeAsync().ConfigureAwait(false);
            // Other disposal callers share this task; no frame operation remains when these are released.
            this.stopping.Dispose();
            this.writes.Dispose();
        }
    }
}
