using System.Diagnostics.CodeAnalysis;
using System.Text;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

public sealed partial class ServiceSubscription
{
    // The frame also carries the envelope fields; this slack keeps a payload under the limit from overflowing the frame.
    private const int CallbackPayloadLimit = ServiceProtocol.MaximumFrameBytes - (64 * 1024);
    private readonly Dictionary<Guid, RunningCallback> callbacks = [];

    /// <summary>Answers callbacks the service sends to this subscriber. Without a handler a callback fails with InvalidState.</summary>
    /// <gotcha>Set it before the subscriber does anything that lets the service call back, such as registering a route.</gotcha>
    public ServiceCallbackHandler? CallbackHandler { get; set; }

    private void StartCallback(CapabilityRequestMessage request)
    {
        CheckBusinessEpoch(request.Epoch);
        if (request.DeadlineMilliseconds <= 0) throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token);
        cancellation.CancelAfter(request.DeadlineMilliseconds);
        var running = new RunningCallback(cancellation);
        lock (this.gate)
        {
            if (!this.callbacks.TryAdd(request.CallbackId, running))
            {
                cancellation.Dispose();
                throw new ServiceProtocolException(ServiceErrorCode.DuplicateRequest);
            }
            running.Completion = Task.Run(() => RunCallbackAsync(request, running));
        }
    }

    private void CancelCallback(CancelMessage cancel)
    {
        CheckBusinessEpoch(cancel.Epoch);
        RunningCallback? running;
        // A Cancel can cross the callback's own result on the wire, so an unknown callback ID is late, not invalid.
        lock (this.gate) running = this.callbacks.GetValueOrDefault(cancel.RequestId);
        running?.Cancel();
    }

    [SuppressMessage("Design", "CA1031", Justification = "A handler failure is answered as a safe Failed result; the subscription stays open.")]
    private async Task RunCallbackAsync(CapabilityRequestMessage request, RunningCallback running)
    {
        try
        {
            ServiceCallbackResult result;
            try
            {
                result = this.CallbackHandler is { } handler
                    ? await handler(request, running.Token).ConfigureAwait(false)
                    : Failed(ServiceErrorCode.InvalidState);
            }
            catch (OperationCanceledException) when (running.Token.IsCancellationRequested) { result = new(ServiceResultStatus.Cancelled); }
            catch (Exception) { result = Failed(ServiceErrorCode.Unavailable); }
            if (result.Payload is { } payload && Encoding.UTF8.GetByteCount(payload.GetRawText()) > CallbackPayloadLimit)
                result = Failed(ServiceErrorCode.InvalidFrame);
            await SendAsync(new CapabilityResultMessage(this.Identity.Epoch, request.CallbackId, result.Status, result.Payload, result.Error),
                this.stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (this.gate) this.callbacks.Remove(request.CallbackId);
            running.Dispose();
        }
    }

    private static ServiceCallbackResult Failed(ServiceErrorCode code) => new(ServiceResultStatus.Failed, Error: new(code, Guid.NewGuid()));

    private sealed class RunningCallback(CancellationTokenSource cancellation) : IDisposable
    {
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal CancellationToken Token { get; } = cancellation.Token;

        // A Cancel can arrive while the callback finishes and disposes its source.
        internal void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => cancellation.Dispose();
    }
}
