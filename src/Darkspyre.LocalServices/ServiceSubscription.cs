using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// An authenticated subscription with heartbeats, requests and invalidations. It owns its connection,
/// never the backend process, and never reconnects or replays a request.
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
                        Finish(ServiceErrorCode.ServiceDraining);
                        return;
                    case ProtocolErrorMessage error:
                        Finish(error.Error.Code);
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
                        Finish(ServiceErrorCode.Unavailable);
                        return;
                    default:
                        Finish(message is PongMessage or DrainingMessage ? ServiceErrorCode.WrongEpoch : ServiceErrorCode.InvalidState);
                        return;
                }
            }
        }
        catch (ServiceProtocolException error) { Finish(error.Code); }
        catch (InvalidDataException) { Finish(ServiceErrorCode.InvalidFrame); }
        catch (IOException) { Finish(ServiceErrorCode.Unavailable); }
        catch (OperationCanceledException) { Finish(ServiceErrorCode.Unavailable); }
        catch (ObjectDisposedException) when (this.stopping.IsCancellationRequested) { }
        catch { Finish(ServiceErrorCode.Unavailable); throw; }
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(ServiceProtocol.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(this.stopping.Token).ConfigureAwait(false))
                await SendAsync(new PingMessage(this.Identity.Epoch), this.stopping.Token).ConfigureAwait(false);
        }
        catch (IOException) { Finish(ServiceErrorCode.Unavailable); }
        catch (OperationCanceledException) { Finish(ServiceErrorCode.Unavailable); }
        catch (ObjectDisposedException) when (this.stopping.IsCancellationRequested) { }
    }

    private async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ServiceProtocol.HeartbeatInterval);
        await this.writes.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await ServiceFrameCodec.WriteAsync(this.peer.Stream, message, deadline.Token).ConfigureAwait(false); }
        catch { Finish(ServiceErrorCode.Unavailable); throw; }
        finally { this.writes.Release(); }
    }

    private void Finish(ServiceErrorCode? reason)
    {
        lock (this.gate)
        {
            if (!this.closed.TrySetResult(reason)) return;
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
            Finish(null);
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
