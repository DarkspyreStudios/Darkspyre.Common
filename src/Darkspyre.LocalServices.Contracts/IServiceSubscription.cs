using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>An authenticated connection carrying requests and invalidations for one subscriber.</summary>
/// <gotcha>Disposal releases this subscription; it does not own the backend process.</gotcha>
public interface IServiceSubscription : IAsyncDisposable
{
    /// <summary>The authenticated service and connection identity.</summary>
    WelcomeMessage Identity { get; }

    /// <summary>Completes when the subscription closes, with a safe failure code when present.</summary>
    Task<ServiceErrorCode?> Closed { get; }

    /// <summary>Why the subscription closed, such as a frame write that failed or the service ending it; null while it is open.</summary>
    string? CloseReason { get; }

    /// <summary>Receives invalidations from the subscription's receive loop.</summary>
    event Action<EventMessage>? EventReceived;

    /// <summary>Reports a call of this subscription that waited at least <see cref="ServiceProtocol.ReportedFrameWait"/>
    /// for its turn on the connection, once, as the call ends.</summary>
    /// <gotcha>A handler that throws is ignored: a report never changes a call's outcome.</gotcha>
    event Action<ServiceCallWait>? CallWaited;

    /// <summary>Answers callbacks the service sends to this subscriber. Without a handler a callback fails with InvalidState.</summary>
    /// <gotcha>Set it before the subscriber does anything that lets the service call back, such as registering a route.</gotcha>
    ServiceCallbackHandler? CallbackHandler { get; set; }

    /// <summary>Submits one operation of <paramref name="requestClass"/> within its deadline and returns its complete terminal reply.</summary>
    /// <gotcha>The caller retains the input stream. Cancellation requests cancellation without replaying the operation and
    /// leaves the subscription open. A closed subscription refuses the call with Unavailable before it sends anything.</gotcha>
    /// <gotcha>A class that does not match what the operation really does moves the limits it is admitted under and
    /// nothing else: a write declared a read can take the slots reads are reserved, and a long save declared a quick
    /// write can hold the store's writer gate past half a second.</gotcha>
    Task<ServiceCallResult> InvokeAsync(string operation, Stream body,
        int deadlineMilliseconds, ServiceRequestClass requestClass, CancellationToken cancellationToken = default);
}
