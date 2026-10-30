using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

public sealed partial class ServiceSubscription
{
    private readonly Dictionary<Guid, PendingCall> pending = [];
    // The connection's admission slots: a call holds one from before it is sent until its result arrives, so the service
    // never refuses it for the connection's request limit. A write also holds one of the write slots, and a long save one
    // of the long-save slots, which is what keeps slots free for the classes that hold fewer (DSA-1291).
    private readonly SemaphoreSlim slots = new(ServiceProtocol.MaximumConnectionRequests);
    private readonly SemaphoreSlim writeSlots = new(ServiceProtocol.MaximumConnectionWrites);
    private readonly SemaphoreSlim longSaveSlots = new(ServiceProtocol.MaximumConnectionLongSaves);
    /// <summary>Receives service invalidations on this subscription.</summary>
    public event Action<EventMessage>? EventReceived;

    /// <summary>
    /// Submits one operation without replay. The caller retains ownership of the input stream. A call waits for the
    /// connection's request slots its <paramref name="requestClass"/> needs before it is sent: one of
    /// <see cref="ServiceProtocol.MaximumConnectionRequests"/> for any class, one of
    /// <see cref="ServiceProtocol.MaximumConnectionWrites"/> as well for a write, and one of
    /// <see cref="ServiceProtocol.MaximumConnectionLongSaves"/> as well for a long save. A call that does not get them
    /// all within its deadline returns Failed with Busy and was never sent. The caller's cancellation and the rest of the
    /// deadline send a Cancel and return the service's answer for this call, such as Cancelled or a result the service
    /// already proved; the subscription stays open. A call the subscription's close interrupts returns OutcomeUnknown.
    /// </summary>
    /// <gotcha>A closed subscription refuses the call with Unavailable before it sends anything, so the owner can send the same
    /// call on a new subscription without replaying it.</gotcha>
    public async Task<ServiceCallResult> InvokeAsync(string operation, Stream body, int deadlineMilliseconds,
        ServiceRequestClass requestClass, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deadlineMilliseconds);
        if (!Enum.IsDefined(requestClass)) throw new ArgumentOutOfRangeException(nameof(requestClass));
        cancellationToken.ThrowIfCancellationRequested();
        long waiting = System.Diagnostics.Stopwatch.GetTimestamp();
        // Scarcest first, and always in this order, so two classes cannot each hold what the other waits for.
        SemaphoreSlim[] reservations = requestClass switch
        {
            ServiceRequestClass.LongSave => [this.longSaveSlots, this.writeSlots, this.slots],
            ServiceRequestClass.QuickWrite => [this.writeSlots, this.slots],
            _ => [this.slots],
        };
        int held = 0;
        try
        {
            for (; held < reservations.Length; held++)
                if (!await reservations[held].WaitAsync(Remaining(deadlineMilliseconds, waiting), cancellationToken).ConfigureAwait(false))
                    return new(ServiceResultStatus.Failed, null, null, new(ServiceErrorCode.Busy, Guid.NewGuid(),
                        $"{operation} waited its whole {deadlineMilliseconds} ms deadline for the connection's "
                        + $"{requestClass} request slots"));
            return await SendRequestAsync(operation, body, Math.Max(1, Remaining(deadlineMilliseconds, waiting)), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { for (int i = held - 1; i >= 0; i--) reservations[i].Release(); }
    }

    // The deadline that is left, never negative: a wait of zero returns at once rather than waiting forever.
    private static int Remaining(int deadlineMilliseconds, long since) =>
        Math.Max(0, deadlineMilliseconds - (int)System.Diagnostics.Stopwatch.GetElapsedTime(since).TotalMilliseconds);

    private async Task<ServiceCallResult> SendRequestAsync(string operation, Stream body, int deadlineMilliseconds, CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid(), transferId = Guid.NewGuid();
        var call = new PendingCall();
        lock (this.gate)
        {
            if (this.closed.Task.IsCompleted) throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
            this.pending.Add(requestId, call);
        }
        try
        {
            await SendAsync(new RequestMessage(this.Identity.Epoch, requestId, operation, 1,
                deadlineMilliseconds, ServiceBodyTransfer.Reference(transferId)), this.stopping.Token).ConfigureAwait(false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(deadlineMilliseconds);
            Task cancel = SendCancelAsync(operation, requestId, call.Result.Task, cancellation.Token);
            try
            {
                if (await call.Accepted.Task.ConfigureAwait(false))
                    await ServiceBodyTransfer.SendAsync(this.Identity.Epoch, requestId, transferId, body, SendAsync, this.stopping.Token).ConfigureAwait(false);
                return await call.Result.Task.ConfigureAwait(false);
            }
            finally { call.Result.TrySetResult(UnknownResult()); await cancel.ConfigureAwait(false); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A failed frame write already closed the subscription with its reason; this names a failed body read.
            Finish(ServiceErrorCode.Unavailable, $"{operation} could not be sent: {Describe(error)}");
            return UnknownResult();
        }
        catch (Exception error)
        {
            // A caller-owned stream failure can leave an unfinished request; close it without replay.
            Finish(ServiceErrorCode.Unavailable, $"{operation}'s request body failed: {Describe(error)}");
            throw;
        }
        finally
        {
            lock (this.gate)
            {
                this.pending.Remove(requestId);
                call.Transfer?.Dispose();
                call.Finished.TrySetResult();
            }
        }
    }

    private async Task SendCancelAsync(string operation, Guid requestId, Task result, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
        if (await Task.WhenAny(result, cancelled.Task).ConfigureAwait(false) != cancelled.Task || result.IsCompleted) return;
        try { await SendAsync(new CancelMessage(this.Identity.Epoch, requestId), this.stopping.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Finish(ServiceErrorCode.Unavailable, $"the Cancel for {operation} could not be sent: {Describe(error)}");
        }
    }
    private async Task ReceiveBusinessAsync(ServiceMessage message)
    {
        if (message is EventMessage notification)
        {
            CheckBusinessEpoch(notification.Epoch);
            this.EventReceived?.Invoke(notification);
            return;
        }
        Guid requestId = message switch
        {
            AcceptedMessage accepted => accepted.RequestId,
            ResultMessage result => result.RequestId,
            BlobChunkMessage chunk => chunk.RequestId,
            _ => throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame),
        };
        PendingCall call;
        lock (this.gate) call = this.pending.GetValueOrDefault(requestId) ?? throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        switch (message)
        {
            case AcceptedMessage accepted:
                CheckBusinessEpoch(accepted.Epoch);
                if (!call.Accepted.TrySetResult(true)) throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
                break;
            case BlobChunkMessage chunk:
                CheckBusinessEpoch(chunk.Epoch);
                if (!call.Accepted.Task.IsCompletedSuccessfully || !call.Accepted.Task.Result || call.Result.Task.IsCompleted)
                    throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
                call.Transfer ??= new ServiceBodyTransfer(chunk.TransferId, new MemoryStream());
                await call.Transfer.AppendAsync(chunk, this.stopping.Token).ConfigureAwait(false);
                break;
            case ResultMessage result:
                CheckBusinessEpoch(result.Epoch);
                byte[]? body = null;
                if (result.Payload is { } reference)
                {
                    Guid transferId = ServiceBodyTransfer.ReadId(reference);
                    if (call.Transfer is null || call.Transfer.TransferId != transferId || !call.Transfer.Complete)
                        throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
                    body = ((MemoryStream)call.Transfer.Body).ToArray();
                }
                else if (call.Transfer is not null) throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
                call.Accepted.TrySetResult(false);
                if (!call.Result.TrySetResult(new(result.Status, result.Revision, body, result.Error)))
                    throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
                break;
        }
    }
    private void CheckBusinessEpoch(Guid epoch)
    {
        if (epoch != this.Identity.Epoch) throw new ServiceProtocolException(ServiceErrorCode.WrongEpoch);
    }
    private static ServiceCallResult UnknownResult() => new(ServiceResultStatus.OutcomeUnknown, null, null, new(ServiceErrorCode.Unavailable, Guid.NewGuid()));
    private sealed class PendingCall
    {
        internal TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ServiceCallResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ServiceBodyTransfer? Transfer { get; set; }
    }
}
