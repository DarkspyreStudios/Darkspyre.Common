using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>A domain-proven result. Cancellation alone proves neither commit nor rollback.</summary>
/// <param name="Status">The request outcome.</param>
/// <param name="Body">The operation-specific result body, when present.</param>
/// <param name="Revision">The resulting revision when the operation supplies one.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceOperationResult(ServiceResultStatus Status, Stream? Body = null, long? Revision = null, ServiceError? Error = null);
/// <summary>Processes a completely validated request body while its admission slot remains occupied.</summary>
public delegate ValueTask<ServiceOperationResult> ServiceRequestHandler(ServiceRequestContext context, CancellationToken cancellationToken);
/// <summary>Server-owned request identity and validated input. The dispatcher owns the stream.</summary>
public sealed class ServiceRequestContext
{
    private readonly ServiceOperationLease lease;
    internal ServiceRequestContext(ServiceOperationLease lease, Stream body) { this.lease = lease; this.Body = body; }
    /// <summary>The authenticated connection identity.</summary>
    public Guid ConnectionId => this.lease.ConnectionId;
    /// <summary>The admitted request envelope.</summary>
    public RequestMessage Request => this.lease.Request;
    /// <summary>The nonnegative time remaining since admission, read from the admission clock.</summary>
    public TimeSpan RemainingTime => this.lease.RemainingTime;
    /// <summary>The complete validated request stream, positioned at zero on entry.</summary>
    public Stream Body { get; }
    /// <summary>Whether this connection still has authority to publish a result.</summary>
    public bool IsCurrent => this.lease.CanPublish;
}

public sealed partial class ServiceControlHost
{
    private readonly FrozenSet<string> operations;
    private readonly ServiceRequestHandler requestHandler;
    private readonly string stagingPath;

    /// <summary>
    /// Publishes an invalidation on existing subscriber transports. A connection that ends while it is being sent the
    /// event misses it; the others still receive it.
    /// </summary>
    public async Task PublishEventAsync(string name, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var message = new EventMessage(this.epoch, name, payload.Clone());
        Connection[] targets;
        lock (this.gate) targets = [.. this.connections.Values.Where(c => c.Subscribed && !c.Finalizing)];
        foreach (Connection connection in targets)
        {
            try { await SendBusinessAsync(connection, message, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { connection.BreakTransport(); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { connection.BreakTransport(); }
            // The connection finished closing during the send; it has nothing left to tell.
            catch (ObjectDisposedException) { }
        }
    }

    private async Task DispatchAsync(Connection connection, Guid connectionId, RequestMessage request)
    {
        Guid transferId = ServiceBodyTransfer.ReadId(request.Payload);
        ServiceRequestAdmission admission;
        lock (this.gate)
        {
            admission = this.Operations.TryAdmit(connectionId, request);
            if (admission.Lease is { } lease)
            {
                try
                {
                    string path = Path.Combine(this.stagingPath, $"{Guid.NewGuid():N}.body");
                    var body = new ServiceBodyTransfer(transferId, new FileStream(path, FileMode.CreateNew,
                        FileAccess.ReadWrite, FileShare.None, ServiceProtocol.MaximumChunkBytes,
                        FileOptions.Asynchronous | FileOptions.DeleteOnClose));
                    var incoming = new IncomingRequest(body);
                    connection.Requests.Add(request.RequestId, incoming);
                    Task work = Task.Run(() => RunRequestAsync(connection, lease, incoming));
                    connection.Work.Add(work);
                    _ = RemoveWorkAsync(connection, work);
                    return;
                }
                catch { lease.Dispose(); throw; }
            }
        }
        if (admission.Error is ServiceErrorCode.WrongEpoch or ServiceErrorCode.InvalidFrame or ServiceErrorCode.DuplicateRequest)
            throw new ServiceProtocolException(admission.Error.Value);
        await SendBusinessAsync(connection, new ResultMessage(this.epoch, request.RequestId,
            ServiceResultStatus.Failed, null, null, new(admission.Error!.Value, Guid.NewGuid())), connection.Stop.Token).ConfigureAwait(false);
    }

    private async Task ReceiveBodyAsync(Connection connection, BlobChunkMessage chunk)
    {
        CheckEpoch(chunk.Epoch);
        IncomingRequest incoming;
        lock (this.gate)
            incoming = connection.Requests.GetValueOrDefault(chunk.RequestId)
                ?? throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
        await incoming.Transfer.AppendAsync(chunk, connection.Stop.Token).ConfigureAwait(false);
        if (incoming.Transfer.Complete) incoming.Ready.TrySetResult();
    }

    private async Task RunRequestAsync(Connection connection, ServiceOperationLease lease, IncomingRequest incoming)
    {
        using (lease)
        using (incoming.Transfer)
        using (var stop = new CancellationTokenSource())
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task cancellation = RelayStopAsync(lease, settled.Task, stop);
            Stream? resultBody = null;
            try
            {
                await SendBusinessAsync(connection, new AcceptedMessage(this.epoch, lease.Request.RequestId), connection.Stop.Token).ConfigureAwait(false);
                await incoming.Ready.Task.WaitAsync(connection.Stop.Token).ConfigureAwait(false);
                ServiceOperationResult result;
                if (lease.StopRequested.IsCompleted || !lease.CanPublish) result = new(ServiceResultStatus.Cancelled);
                else
                {
                    try
                    {
                        result = await this.requestHandler(new(lease, incoming.Transfer.Body), stop.Token).ConfigureAwait(false);
                        resultBody = result.Body;
                        if (!Enum.IsDefined(result.Status) || (result.Error is { } error && (!Enum.IsDefined(error.Code) || error.CorrelationId == Guid.Empty)))
                            throw new InvalidDataException("The service handler returned an invalid result.");
                    }
                    catch (OperationCanceledException) { result = new(ServiceResultStatus.OutcomeUnknown); }
                    catch (Exception error)
                    {
                        result = new(ServiceResultStatus.OutcomeUnknown, Error: new(ServiceErrorCode.Unavailable, Guid.NewGuid(),
                            $"({error.GetType().FullName}): {error.Message}"));
                    }
                }
                JsonElement? reference = null;
                if (result.Body is { } body)
                {
                    Guid transferId = Guid.NewGuid();
                    await ServiceBodyTransfer.SendAsync(this.epoch, lease.Request.RequestId, transferId, body,
                        (message, token) => SendBusinessAsync(connection, message, token), connection.Stop.Token).ConfigureAwait(false);
                    reference = ServiceBodyTransfer.Reference(transferId);
                }
                // Connection revocation closes the transport; cancellation alone cannot erase a proven domain result.
                if (!lease.CanPublish) return;
                await SendBusinessAsync(connection, new ResultMessage(this.epoch, lease.Request.RequestId,
                    result.Status, result.Revision, reference, result.Error), connection.Stop.Token).ConfigureAwait(false);
            }
            catch (IOException) { connection.BreakTransport(); }
            catch (OperationCanceledException) { connection.BreakTransport(); }
            catch (ObjectDisposedException) { connection.BreakTransport(); }
            finally
            {
                try { if (resultBody is not null) await resultBody.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    settled.TrySetResult();
                    try { await cancellation.ConfigureAwait(false); }
                    finally { lock (this.gate) connection.Requests.Remove(lease.Request.RequestId); }
                }
            }
        }
    }
    private async Task RemoveWorkAsync(Connection connection, Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { connection.BreakTransport(); }
        finally
        {
            // Failed cleanup stays in the join set so peer/backend disposal observes the failure.
            if (work.IsCompletedSuccessfully) lock (this.gate) connection.Work.Remove(work);
        }
    }
    private static async Task RelayStopAsync(ServiceOperationLease lease, Task settled, CancellationTokenSource cancellation)
    {
        if (await Task.WhenAny(lease.StopRequested, settled).ConfigureAwait(false) == lease.StopRequested)
            await cancellation.CancelAsync().ConfigureAwait(false);
    }
    private static Task SendBusinessAsync(Connection connection, ServiceMessage message, CancellationToken token) =>
        connection.SendAsync(message, token);
    private sealed class IncomingRequest(ServiceBodyTransfer transfer)
    {
        internal ServiceBodyTransfer Transfer { get; } = transfer;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
