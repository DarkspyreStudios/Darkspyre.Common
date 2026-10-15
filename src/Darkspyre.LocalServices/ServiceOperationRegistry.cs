using System.Collections.Frozen;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>The reason admitted work must stop.</summary>
public enum ServiceOperationStopReason
{
    /// <summary>The caller requests cancellation.</summary>
    ClientCancelled,
    /// <summary>The admitted deadline expires.</summary>
    DeadlineElapsed,
    /// <summary>The request authority is revoked.</summary>
    CapabilityRevoked,
    /// <summary>The owning subscriber disconnects.</summary>
    ConnectionClosed,
    /// <summary>The service begins draining.</summary>
    ServiceDraining,
}
/// <summary>The admitted lease or the safe rejection code.</summary>
/// <param name="Lease">The admitted operation lease, or null on rejection.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceRequestAdmission(ServiceOperationLease? Lease, ServiceErrorCode? Error);

/// <summary>
/// Backend admission and immutable context identities. Trusted registration supplies operation allowlists;
/// domain/host code still enforces file, account and source authority. This registry does not dispatch work.
/// </summary>
public sealed class ServiceOperationRegistry
{
    private readonly object gate;
    private readonly Guid epoch;
    private readonly TimeProvider time;
    private readonly Action? maintainLifecycle;
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly Dictionary<(Guid Connection, Guid Request), ServiceOperationLease> active = [];

    /// <summary>Creates admission accounting for one service epoch.</summary>
    public ServiceOperationRegistry(Guid epoch, TimeProvider? timeProvider = null) : this(epoch, new object(), timeProvider, null) { }

    internal ServiceOperationRegistry(Guid epoch, object gate, TimeProvider? timeProvider, Action? maintainLifecycle)
    {
        if (epoch == Guid.Empty) throw new ArgumentException("An epoch is required.", nameof(epoch));
        this.epoch = epoch;
        this.gate = gate;
        this.time = timeProvider ?? TimeProvider.System;
        this.maintainLifecycle = maintainLifecycle;
    }

    /// <summary>The number of admitted operations that still hold a lease.</summary>
    public int ActiveCount { get { lock (this.gate) return this.active.Count; } }

    /// <summary>Called by the trusted lifecycle owner only after subscription admission.</summary>
    public void OpenConnection(Guid connectionId)
    {
        if (connectionId == Guid.Empty) throw new ArgumentException("A connection identity is required.", nameof(connectionId));
        lock (this.gate)
        {
            if (this.active.Keys.Any(key => key.Connection == connectionId) || !this.connections.TryAdd(connectionId, new()))
                throw new InvalidOperationException("The connection is already admitted or retains unfinished work.");
        }
    }

    /// <summary>
    /// Called by the trusted authority bridge, never from model payload grants. Revision identities increase
    /// across this connection, including revoked revisions. Operation strings are exact and copied immutably.
    /// </summary>
    public void RegisterContext(Guid connectionId, long revision, IEnumerable<string> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        FrozenSet<string> allowed = operations.ToFrozenSet(StringComparer.Ordinal);
        if (allowed.Any(operation => !ValidOperation(operation))) throw new ArgumentException("An operation name is invalid.", nameof(operations));
        lock (this.gate)
        {
            Maintain();
            if (!this.connections.TryGetValue(connectionId, out Connection? connection)) throw new InvalidOperationException("The connection has no live subscription.");
            if (revision <= connection.HighestRevision) throw new ArgumentOutOfRangeException(nameof(revision), "Context identities cannot be reused.");
            connection.HighestRevision = revision;
            connection.Contexts.Add(revision, allowed);
        }
    }

    /// <summary>The peer supplies connection identity; a Request envelope cannot choose another connection.</summary>
    public ServiceRequestAdmission TryAdmit(Guid connectionId, RequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (this.gate)
        {
            Maintain();
            if (request.Epoch != this.epoch) return new(null, ServiceErrorCode.WrongEpoch);
            if (request.RequestId == Guid.Empty || !ValidOperation(request.Operation) || request.DeadlineMilliseconds <= 0
                || request.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                return new(null, ServiceErrorCode.InvalidFrame);
            if (!this.connections.TryGetValue(connectionId, out Connection? connection)
                || !connection.Contexts.TryGetValue(request.ContextRevision, out FrozenSet<string>? context)
                || !context.Contains(request.Operation)) return new(null, ServiceErrorCode.CapabilityRevoked);
            if (this.active.ContainsKey((connectionId, request.RequestId))) return new(null, ServiceErrorCode.DuplicateRequest);
            if (this.active.Count >= ServiceProtocol.MaximumServiceRequests || connection.Active >= ServiceProtocol.MaximumConnectionRequests)
                return new(null, ServiceErrorCode.Busy);
            var lease = new ServiceOperationLease(this, connectionId, request with { Payload = request.Payload.Clone() }, this.time.GetTimestamp());
            this.active.Add((connectionId, request.RequestId), lease);
            connection.Active++;
            return new(lease, null);
        }
    }

    /// <summary>Cancellation requests cooperation; it never reports rollback or releases an occupied work slot.</summary>
    public bool Cancel(Guid connectionId, Guid requestId)
    {
        lock (this.gate)
        {
            Maintain();
            if (!this.active.TryGetValue((connectionId, requestId), out ServiceOperationLease? lease)) return false;
            lease.Stop(ServiceOperationStopReason.ClientCancelled);
            return true;
        }
    }

    /// <summary>Revokes an authority context and requests its active operations to stop.</summary>
    public void RevokeContext(Guid connectionId, long revision)
    {
        lock (this.gate)
        {
            if (this.connections.TryGetValue(connectionId, out Connection? connection)) connection.Contexts.Remove(revision);
            foreach (ServiceOperationLease lease in this.active.Values)
                if (lease.ConnectionId == connectionId && lease.Request.ContextRevision == revision)
                    lease.Stop(ServiceOperationStopReason.CapabilityRevoked);
        }
    }

    /// <summary>Closes admission for a connection and requests its operations to stop.</summary>
    public void CloseConnection(Guid connectionId, bool draining = false)
    {
        lock (this.gate)
        {
            this.connections.Remove(connectionId);
            foreach (ServiceOperationLease lease in this.active.Values)
                if (lease.ConnectionId == connectionId)
                    lease.Stop(draining ? ServiceOperationStopReason.ServiceDraining : ServiceOperationStopReason.ConnectionClosed);
        }
    }

    /// <summary>Called independently of business handlers. Stop continuations never run under this registry's lock.</summary>
    public void Tick() { lock (this.gate) Maintain(); }

    private void Maintain()
    {
        this.maintainLifecycle?.Invoke();
        long now = this.time.GetTimestamp();
        foreach (ServiceOperationLease lease in this.active.Values)
            if (this.time.GetElapsedTime(lease.Started, now).TotalMilliseconds >= lease.Request.DeadlineMilliseconds)
                lease.Stop(ServiceOperationStopReason.DeadlineElapsed);
    }

    internal TimeSpan RemainingTime(ServiceOperationLease lease)
    {
        TimeSpan remaining = TimeSpan.FromMilliseconds(lease.Request.DeadlineMilliseconds) - this.time.GetElapsedTime(lease.Started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal bool CanPublish(ServiceOperationLease lease)
    {
        lock (this.gate)
        {
            Maintain();
            return this.active.TryGetValue((lease.ConnectionId, lease.Request.RequestId), out ServiceOperationLease? current)
                && ReferenceEquals(current, lease)
                && this.connections.TryGetValue(lease.ConnectionId, out Connection? connection)
                && connection.Contexts.ContainsKey(lease.Request.ContextRevision);
        }
    }

    internal void Release(ServiceOperationLease lease)
    {
        lock (this.gate)
        {
            if (!this.active.TryGetValue((lease.ConnectionId, lease.Request.RequestId), out ServiceOperationLease? current) || !ReferenceEquals(current, lease)) return;
            this.active.Remove((lease.ConnectionId, lease.Request.RequestId));
            if (this.connections.TryGetValue(lease.ConnectionId, out Connection? connection)) connection.Active--;
        }
    }

    private static bool ValidOperation(string operation) => !string.IsNullOrWhiteSpace(operation)
        && operation.Length <= 128 && !operation.Any(char.IsControl);

    private sealed class Connection
    {
        public long HighestRevision { get; set; }
        public int Active { get; set; }
        public Dictionary<long, FrozenSet<string>> Contexts { get; } = [];
    }
}

/// <summary>
/// An admitted server-stamped request. Its work slot stays occupied until Dispose, even after stop is requested.
/// StopRequested asks the dispatcher to cancel; CanPublish independently fences revoked result authority.
/// </summary>
public sealed class ServiceOperationLease : IDisposable
{
    private readonly ServiceOperationRegistry registry;
    private readonly TaskCompletionSource<ServiceOperationStopReason> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ServiceOperationLease(ServiceOperationRegistry registry, Guid connectionId, RequestMessage request, long started)
    {
        this.registry = registry;
        this.ConnectionId = connectionId;
        this.Request = request;
        this.Started = started;
    }
    internal long Started { get; }
    internal TimeSpan RemainingTime => this.registry.RemainingTime(this);
    /// <summary>The connection that owns the admitted request.</summary>
    public Guid ConnectionId { get; }
    /// <summary>The admitted request with its captured payload.</summary>
    public RequestMessage Request { get; }
    /// <summary>Completes with the first reason the operation must stop.</summary>
    public Task<ServiceOperationStopReason> StopRequested => this.stopped.Task;
    /// <summary>Whether the lease still permits result publication.</summary>
    public bool CanPublish => this.registry.CanPublish(this);
    internal void Stop(ServiceOperationStopReason reason) => this.stopped.TrySetResult(reason);
    /// <summary>Releases the admitted operation slot.</summary>
    public void Dispose() => this.registry.Release(this);
}
