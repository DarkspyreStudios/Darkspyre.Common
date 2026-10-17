using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

public sealed partial class ServiceControlHost
{
    /// <summary>
    /// Asks the subscriber on <paramref name="connectionId"/> to run <paramref name="operation"/> for the active request
    /// <paramref name="requestId"/>, and returns its answer.
    /// </summary>
    /// <gotcha>A connection that is gone, or closes before it answers, returns Failed with Unavailable. Cancellation sends
    /// the subscriber a Cancel naming the callback and returns Cancelled without waiting for its answer.</gotcha>
    public async Task<ServiceCallbackResult> CallbackAsync(Guid connectionId, Guid requestId, string operation, JsonElement payload,
        int deadlineMilliseconds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deadlineMilliseconds);
        Guid callbackId = Guid.NewGuid();
        var answer = new TaskCompletionSource<ServiceCallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Connection? connection;
        lock (this.gate)
        {
            connection = this.connections.GetValueOrDefault(connectionId);
            if (connection is null || !connection.Subscribed || connection.Finalizing) return Unavailable();
            connection.Callbacks.Add(callbackId, answer);
        }
        try
        {
            await SendBusinessAsync(connection, new CapabilityRequestMessage(this.epoch, callbackId, requestId, null, 1, operation,
                deadlineMilliseconds, payload.Clone()), connection.Stop.Token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(deadlineMilliseconds);
            try { return await answer.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !connection.Stop.IsCancellationRequested)
            {
                await SendBusinessAsync(connection, new CancelMessage(this.epoch, callbackId), connection.Stop.Token).ConfigureAwait(false);
                return new(ServiceResultStatus.Cancelled);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            connection.BreakTransport();
            return Unavailable();
        }
        finally
        {
            lock (this.gate) connection.Callbacks.Remove(callbackId);
        }
    }

    private void ReceiveCallbackResult(Connection connection, CapabilityResultMessage result)
    {
        CheckEpoch(result.Epoch);
        if (!Enum.IsDefined(result.Status) || (result.Error is { } error && (!Enum.IsDefined(error.Code) || error.CorrelationId == Guid.Empty)))
            throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        TaskCompletionSource<ServiceCallbackResult>? answer;
        // A result can cross a cancel or deadline on the wire, so an unknown callback ID is late, not invalid.
        lock (this.gate) answer = connection.Callbacks.GetValueOrDefault(result.CallbackId);
        answer?.TrySetResult(new(result.Status, result.Payload, result.Error));
    }

    private static ServiceCallbackResult Unavailable() => new(ServiceResultStatus.Failed, Error: new(ServiceErrorCode.Unavailable, Guid.NewGuid()));
}
