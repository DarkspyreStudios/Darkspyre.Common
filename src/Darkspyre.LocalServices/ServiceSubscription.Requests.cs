using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

public sealed partial class ServiceSubscription
{
    private readonly Dictionary<Guid, PendingCall> pending = [];
    /// <summary>Receives service invalidations on this subscription.</summary>
    public event Action<EventMessage>? EventReceived;

    /// <summary>
    /// Submits one operation without replay. The caller retains ownership of the input stream. The caller's cancellation and
    /// the deadline send a Cancel and return the service's answer for this call, such as Cancelled or a result the service
    /// already proved; the subscription stays open. A call the subscription's close interrupts returns OutcomeUnknown.
    /// </summary>
    /// <gotcha>A closed subscription refuses the call with Unavailable before it sends anything, so the owner can send the same
    /// call on a new subscription without replaying it.</gotcha>
    public async Task<ServiceCallResult> InvokeAsync(string operation, Stream body, int deadlineMilliseconds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deadlineMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();
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
