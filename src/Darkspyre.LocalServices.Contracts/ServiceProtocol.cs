namespace Darkspyre.LocalServices.Contracts;

/// <summary>Shared wire bounds and lifecycle deadlines. Protocol identity is independent of plugin versions.</summary>
public static class ServiceProtocol
{
    /// <summary>The supported wire protocol version.</summary>
    public const int Version = 4;
    /// <summary>The maximum frame payload size in bytes: the JSON message, or a chunk header and its bytes.</summary>
    public const int MaximumFrameBytes = 1024 * 1024;
    /// <summary>The maximum body chunk size in bytes: the frame payload less the 93-byte final chunk header, so one chunk fills one frame.</summary>
    public const int MaximumChunkBytes = MaximumFrameBytes - 93;
    /// <summary>The maximum admitted requests per connection.</summary>
    public const int MaximumConnectionRequests = 32;
    /// <summary>The maximum admitted requests across the service.</summary>
    public const int MaximumServiceRequests = 256;
    /// <summary>The launch-attempt limit within one launch window.</summary>
    public const int MaximumLaunchAttempts = 3;
    /// <summary>The interval that bounds repeated launch attempts.</summary>
    public static TimeSpan LaunchWindow => TimeSpan.FromSeconds(60);
    /// <summary>The time allowed for the first subscriber to arrive.</summary>
    public static TimeSpan FirstSubscriberDeadline => TimeSpan.FromSeconds(15);
    /// <summary>The connection and readiness deadline.</summary>
    public static TimeSpan ConnectDeadline => TimeSpan.FromSeconds(30);
    /// <summary>
    /// The time allowed for the last frame written as a connection closes: the subscriber's Unsubscribe, or the service's
    /// Draining or refusal. A live connection's frames have no deadline; only its end closes it.
    /// </summary>
    public static TimeSpan ClosingWriteDeadline => TimeSpan.FromSeconds(5);
    /// <summary>The grace period for admitted work during drain.</summary>
    public static TimeSpan GracefulDrainDeadline => TimeSpan.FromSeconds(10);
    /// <summary>The maximum shutdown wait.</summary>
    public static TimeSpan ShutdownDeadline => TimeSpan.FromSeconds(15);
}

/// <summary>A fixed service family. Family ownership does not vary by binary or protocol version.</summary>
public enum ServiceFamily
{
    /// <summary>The shared Memory service family.</summary>
    Memory,
    /// <summary>The ticket service family.</summary>
    Tickets,
    /// <summary>The host-tools service family, which routes MCP tool calls from agent CLIs to the host session that owns them.</summary>
    HostTools,
    /// <summary>The Chromium service family, which runs the panel browser's CEF engine for the Chromium plugin.</summary>
    Chromium,
}

/// <summary>The service process lifecycle.</summary>
public enum ServiceLifecycleState
{
    /// <summary>The process initializes before admitting subscribers.</summary>
    Starting,
    /// <summary>The service admits work.</summary>
    Active,
    /// <summary>The service joins admitted work before exit.</summary>
    Draining,
    /// <summary>The process has exited.</summary>
    Exited,
}
/// <summary>The reported operation outcome.</summary>
public enum ServiceResultStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,
    /// <summary>The operation returned a safe error.</summary>
    Failed,
    /// <summary>Cancellation completed.</summary>
    Cancelled,
    /// <summary>The caller cannot establish the operation’s outcome.</summary>
    OutcomeUnknown,
}
/// <summary>The durable store’s readiness state.</summary>
public enum StoreHealth
{
    /// <summary>The store has not been opened.</summary>
    Unopened,
    /// <summary>The store is opening.</summary>
    Opening,
    /// <summary>The store accepts operations.</summary>
    Ready,
    /// <summary>The store is preparing its resources.</summary>
    Preparing,
    /// <summary>The store cannot serve operations.</summary>
    Unavailable,
    /// <summary>The store is isolated after a health failure.</summary>
    Quarantined,
}

/// <summary>Stable safe errors. Internal exceptions belong in correlated service diagnostics.</summary>
public enum ServiceErrorCode
{
    /// <summary>The protocol versions are incompatible.</summary>
    IncompatibleProtocol,
    /// <summary>The expected store identity differs.</summary>
    StoreMismatch,
    /// <summary>Another owner holds the store.</summary>
    StoreInUse,
    /// <summary>The service no longer admits work.</summary>
    ServiceDraining,
    /// <summary>The requested capability is unavailable.</summary>
    Unavailable,
    /// <summary>The admission capacity is exhausted.</summary>
    Busy,
    /// <summary>The authority for the capability was revoked.</summary>
    CapabilityRevoked,
    /// <summary>The received envelope is invalid.</summary>
    InvalidFrame,
    /// <summary>The operation is invalid in the current state.</summary>
    InvalidState,
    /// <summary>The message belongs to another service process.</summary>
    WrongEpoch,
    /// <summary>The request identity was already used.</summary>
    DuplicateRequest,
    /// <summary>The runtime endpoint path exceeds the platform limit.</summary>
    RuntimePathTooLong,
}
