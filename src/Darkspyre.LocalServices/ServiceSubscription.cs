using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// An authenticated subscription with requests and invalidations. It owns its connection, never the backend process. One
/// call's cancellation, deadline or wait for the write path ends only that call; the subscription closes only when its
/// connection fails, the service ends it or the subscriber disposes it, and <see cref="CloseReason"/> says which. No timer
/// closes it: a busy or slow service is still connected.
/// A closed subscription stays closed: it never reconnects or replays a request. Its owner opens a new one.
/// </summary>
public sealed partial class ServiceSubscription : IServiceSubscription
{
    private readonly ServicePeer peer;
    private readonly FrameGate frames = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<ServiceErrorCode?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Task receiver;
    private Task? disposal;
    private string? closeReason;

    private ServiceSubscription(ServicePeer peer)
    {
        this.peer = peer;
        this.receiver = ReceiveAsync();
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
            if (response is ProtocolErrorMessage error) throw new ServiceProtocolException(error.Error);
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
                ServiceMessage? message = await ServiceFrameCodec.ReadAsync(this.peer.Stream, this.stopping.Token).ConfigureAwait(false);
                switch (message)
                {
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
                        Finish(message is DrainingMessage ? ServiceErrorCode.WrongEpoch : ServiceErrorCode.InvalidState,
                            $"the service sent an unexpected {message.GetType().Name}");
                        return;
                }
            }
        }
        catch (ServiceProtocolException error) { Finish(error.Code, $"a frame from the service was refused: {error.Code}"); }
        catch (InvalidDataException error) { Finish(ServiceErrorCode.InvalidFrame, $"a frame from the service could not be read: {error.Message}"); }
        catch (IOException error) { Finish(ServiceErrorCode.Unavailable, $"reading from the service failed: {Describe(error)}"); }
        // Only the subscription's own close stops a read, and that close already says why.
        catch (OperationCanceledException) when (this.stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (this.stopping.IsCancellationRequested) { }
        catch (Exception error) { Finish(ServiceErrorCode.Unavailable, $"the receive loop failed: {Describe(error)}"); throw; }
    }

    // A frame write waits as long as the service takes to read it, and the gate writes a frame that starts or stops a
    // call before a queued body chunk. Only a write that fails, or one the subscription's close interrupts, closes the
    // subscription: it may leave a partial frame on the stream. <paramref name="waits"/> collects the gate wait of the
    // frames of one call.
    private async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken, CallWaits? waits = null)
    {
        using FrameGate.Hold hold = await this.frames.EnterAsync(message, cancellationToken).ConfigureAwait(false);
        waits?.Add(hold.Waited);
        try { await ServiceFrameCodec.WriteAsync(this.peer.Stream, message, cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            Finish(ServiceErrorCode.Unavailable, $"writing a {message.GetType().Name} failed: {Describe(error)}");
            throw;
        }
    }

    // The service answers by draining: every connection, this one too, closes with ServiceDraining.
    internal Task RequestStopAsync(CancellationToken cancellationToken)
        => SendAsync(new StopMessage(this.Identity.Epoch), cancellationToken);

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
            {
                // The closing frame is the one write with a deadline: a service that never reads it cannot hold disposal.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token);
                deadline.CancelAfter(ServiceProtocol.ClosingWriteDeadline);
                await SendAsync(new UnsubscribeMessage(this.Identity.Epoch), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Finish(null, "the subscriber closed it");
            await this.receiver.ConfigureAwait(false);
            Task[] calls;
            lock (this.gate) calls = [.. this.pending.Values.Select(call => call.Finished.Task), .. this.callbacks.Values.Select(callback => callback.Completion)];
            await Task.WhenAll(calls).ConfigureAwait(false);
            await this.peer.DisposeAsync().ConfigureAwait(false);
            // Other disposal callers share this task; no frame operation remains when these are released.
            this.stopping.Dispose();
            this.frames.Dispose();
        }
    }
}
