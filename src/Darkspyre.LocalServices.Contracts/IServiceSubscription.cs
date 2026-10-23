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

    /// <summary>Answers callbacks the service sends to this subscriber. Without a handler a callback fails with InvalidState.</summary>
    /// <gotcha>Set it before the subscriber does anything that lets the service call back, such as registering a route.</gotcha>
    ServiceCallbackHandler? CallbackHandler { get; set; }

    /// <summary>Submits one operation within its deadline and returns its complete terminal reply.</summary>
    /// <gotcha>The caller retains the input stream. Cancellation requests cancellation without replaying the operation and
    /// leaves the subscription open. A closed subscription refuses the call with Unavailable before it sends anything.</gotcha>
    Task<ServiceCallResult> InvokeAsync(string operation, Stream body,
        int deadlineMilliseconds, CancellationToken cancellationToken = default);
}
