using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>A transition result. The connection owner must revoke every removed subscriber's authority.</summary>
/// <param name="State">The current service lifecycle state.</param>
/// <param name="Succeeded">Whether the requested subscriber transition succeeds.</param>
/// <param name="SubscriberCount">The remaining live subscriber count.</param>
/// <param name="RemovedSubscribers">The subscriber identities removed by this transition.</param>
/// <param name="GracefulDeadlineReached">Whether the graceful drain deadline has elapsed.</param>
/// <param name="ShutdownDeadlineReached">Whether the process shutdown deadline has elapsed.</param>
public sealed record SubscriberUpdate(ServiceLifecycleState State, bool Succeeded, int SubscriberCount,
    IReadOnlyList<Guid> RemovedSubscribers, bool GracefulDeadlineReached, bool ShutdownDeadlineReached);

/// <summary>
/// Serializes subscription admission, lease expiry and last-subscriber drain. Connection identities are assigned
/// after authentication. A host calls Tick independently of business work to expire leases and enforce deadlines.
/// </summary>
public sealed class SubscriberRegistry
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly long startedAt;
    private readonly Dictionary<Guid, long> subscribers = [];
    private ServiceLifecycleState state = ServiceLifecycleState.Starting;
    private long drainStartedAt;

    /// <summary>Creates subscriber accounting with an optional startup clock.</summary>
    public SubscriberRegistry(TimeProvider? timeProvider = null, long? startupTimestamp = null)
    {
        this.time = timeProvider ?? TimeProvider.System;
        long now = this.time.GetTimestamp();
        if (startupTimestamp > now) throw new ArgumentOutOfRangeException(nameof(startupTimestamp));
        this.startedAt = startupTimestamp ?? now;
    }

    /// <summary>Admits a new authenticated connection. Duplicate subscriptions are rejected without renewal.</summary>
    public SubscriberUpdate Subscribe(Guid connectionId)
    {
        RequireIdentity(connectionId);
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            List<Guid> removed = Advance(now);
            if (this.state is ServiceLifecycleState.Draining or ServiceLifecycleState.Exited)
                return Result(false, removed, now);
            if (!this.subscribers.TryAdd(connectionId, now)) return Result(false, removed, now);
            this.state = ServiceLifecycleState.Active;
            return Result(true, removed, now);
        }
    }

    /// <summary>A heartbeat renews an existing unexpired lease. It cannot restore an expired subscriber.</summary>
    public SubscriberUpdate Heartbeat(Guid connectionId)
    {
        RequireIdentity(connectionId);
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            List<Guid> removed = Advance(now);
            bool found = this.subscribers.ContainsKey(connectionId);
            if (found) this.subscribers[connectionId] = now;
            return Result(found, removed, now);
        }
    }

    /// <summary>Validates admission without extending a heartbeat lease.</summary>
    public SubscriberUpdate CheckAdmission(Guid connectionId)
    {
        RequireIdentity(connectionId);
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            List<Guid> removed = Advance(now);
            return Result(this.subscribers.ContainsKey(connectionId), removed, now);
        }
    }

    /// <summary>Releases a subscriber and evaluates whether the service must drain.</summary>
    public SubscriberUpdate Unsubscribe(Guid connectionId)
    {
        RequireIdentity(connectionId);
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            List<Guid> removed = Advance(now);
            bool found = this.subscribers.Remove(connectionId);
            if (found) removed.Add(connectionId);
            if (this.state == ServiceLifecycleState.Active && this.subscribers.Count == 0) StartDrain(now);
            return Result(found, removed, now);
        }
    }

    /// <summary>Expires missed heartbeats and evaluates lifecycle deadlines.</summary>
    public SubscriberUpdate Tick()
    {
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            return Result(true, Advance(now), now);
        }
    }

    /// <summary>Fences all clients when the backend cannot continue. The host still attempts bounded cleanup.</summary>
    public SubscriberUpdate BeginDrain()
    {
        lock (this.gate)
        {
            long now = this.time.GetTimestamp();
            List<Guid> removed = [.. this.subscribers.Keys];
            this.subscribers.Clear();
            if (this.state is ServiceLifecycleState.Starting or ServiceLifecycleState.Active) StartDrain(now);
            return Result(true, removed, now);
        }
    }

    /// <summary>Marks completion only after the backend has finished cleanup. It never releases OS ownership.</summary>
    public SubscriberUpdate CompleteShutdown()
    {
        lock (this.gate)
        {
            if (this.state != ServiceLifecycleState.Draining)
                throw new InvalidOperationException("Cleanup can complete only for a draining service.");
            this.state = ServiceLifecycleState.Exited;
            return Result(true, [], this.time.GetTimestamp());
        }
    }

    private List<Guid> Advance(long now)
    {
        List<Guid> removed = [];
        foreach ((Guid id, long lastHeartbeat) in this.subscribers)
            if (this.time.GetElapsedTime(lastHeartbeat, now) >= ServiceProtocol.SubscriberExpiry) removed.Add(id);
        foreach (Guid id in removed) this.subscribers.Remove(id);
        if ((this.state == ServiceLifecycleState.Starting && this.time.GetElapsedTime(this.startedAt, now) >= ServiceProtocol.FirstSubscriberDeadline)
            || (this.state == ServiceLifecycleState.Active && this.subscribers.Count == 0)) StartDrain(now);
        return removed;
    }

    private void StartDrain(long now)
    {
        this.state = ServiceLifecycleState.Draining;
        this.drainStartedAt = now;
    }

    private SubscriberUpdate Result(bool succeeded, List<Guid> removed, long now)
    {
        TimeSpan elapsed = this.state == ServiceLifecycleState.Draining
            ? this.time.GetElapsedTime(this.drainStartedAt, now) : TimeSpan.Zero;
        return new(this.state, succeeded, this.subscribers.Count, removed.AsReadOnly(),
            elapsed >= ServiceProtocol.GracefulDrainDeadline, elapsed >= ServiceProtocol.ShutdownDeadline);
    }

    private static void RequireIdentity(Guid connectionId)
    {
        if (connectionId == Guid.Empty) throw new ArgumentException("A connection identity is required.", nameof(connectionId));
    }
}
