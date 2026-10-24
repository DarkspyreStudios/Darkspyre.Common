using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.LocalServices.Contracts;

/// <summary>
/// Typed envelopes for an authenticated service connection. Capabilities are not serialized here. Every message except
/// <see cref="BlobChunkMessage"/> travels as JSON; a chunk travels as a binary frame and has no JSON form.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(SubscribeMessage), "subscribe")]
[JsonDerivedType(typeof(SubscribeAckMessage), "subscribe-ack")]
[JsonDerivedType(typeof(UnsubscribeMessage), "unsubscribe")]
[JsonDerivedType(typeof(DrainingMessage), "draining")]
[JsonDerivedType(typeof(HealthMessage), "health")]
[JsonDerivedType(typeof(ProtocolErrorMessage), "protocol-error")]
[JsonDerivedType(typeof(RequestMessage), "request")]
[JsonDerivedType(typeof(AcceptedMessage), "accepted")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(CancelMessage), "cancel")]
[JsonDerivedType(typeof(EventMessage), "event")]
[JsonDerivedType(typeof(CapabilityRequestMessage), "capability-request")]
[JsonDerivedType(typeof(CapabilityResultMessage), "capability-result")]
public abstract record ServiceMessage;

/// <summary>Requests a handshake with the expected service family and store.</summary>
/// <param name="Service">The fixed service family.</param>
/// <param name="ProtocolVersion">The wire protocol version.</param>
/// <param name="ExpectedStoreId">The store identity the subscriber expects.</param>
/// <param name="ClientBuildDigest">The subscriber’s expected package digest.</param>
public sealed record HelloMessage(ServiceFamily Service, int ProtocolVersion, Guid ExpectedStoreId, string ClientBuildDigest) : ServiceMessage;
/// <summary>Identifies the service process, store and negotiated connection.</summary>
/// <param name="Service">The fixed service family.</param>
/// <param name="ProtocolVersion">The wire protocol version.</param>
/// <param name="StoreId">The durable store identity.</param>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="ConnectionId">The server-assigned connection identity.</param>
/// <param name="BuildDigest">The running service package digest.</param>
/// <param name="LayoutId">The installed package layout identity.</param>
public sealed record WelcomeMessage(ServiceFamily Service, int ProtocolVersion, Guid StoreId, Guid Epoch, Guid ConnectionId,
    string BuildDigest, string LayoutId) : ServiceMessage;
/// <summary>Acquires a subscriber lease in the current process epoch.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
public sealed record SubscribeMessage(Guid Epoch) : ServiceMessage;
/// <summary>Acknowledges the subscriber lease for this connection.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="ConnectionId">The server-assigned connection identity.</param>
public sealed record SubscribeAckMessage(Guid Epoch, Guid ConnectionId) : ServiceMessage;
/// <summary>Releases this connection’s subscriber lease.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
public sealed record UnsubscribeMessage(Guid Epoch) : ServiceMessage;
/// <summary>Reports that the service no longer admits work.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
public sealed record DrainingMessage(Guid Epoch) : ServiceMessage;
/// <summary>Publishes the current store health and optional safe error.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="StoreId">The durable store identity.</param>
/// <param name="State">The reported store lifecycle state.</param>
/// <param name="Error">The safe error, when the operation fails.</param>
public sealed record HealthMessage(Guid Epoch, Guid StoreId, StoreHealth State, ServiceError? Error) : ServiceMessage;
/// <summary>Reports a rejected protocol exchange.</summary>
/// <param name="Error">The safe error, when the operation fails.</param>
public sealed record ProtocolErrorMessage(ServiceError Error) : ServiceMessage;

/// <summary>ContextRevision names a server-registered immutable authority context, not a caller's grant.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="RequestId">The originating request identity.</param>
/// <param name="Operation">The named operation to dispatch.</param>
/// <param name="ContextRevision">The server-registered immutable authority context revision.</param>
/// <param name="DeadlineMilliseconds">The operation deadline duration in milliseconds.</param>
/// <param name="Payload">The operation-specific JSON payload.</param>
public sealed record RequestMessage(Guid Epoch, Guid RequestId, string Operation, long ContextRevision,
    int DeadlineMilliseconds, JsonElement Payload) : ServiceMessage;

/// <summary>Admission acknowledgement. It does not acknowledge a transaction commit.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="RequestId">The originating request identity.</param>
public sealed record AcceptedMessage(Guid Epoch, Guid RequestId) : ServiceMessage;
/// <summary>Returns the final status and optional result of an admitted request.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="RequestId">The originating request identity.</param>
/// <param name="Status">The operation’s completion or cancellation status.</param>
/// <param name="Revision">The resulting revision when the operation supplies one.</param>
/// <param name="Payload">The operation-specific JSON payload.</param>
/// <param name="Error">The safe error, when the operation fails.</param>
public sealed record ResultMessage(Guid Epoch, Guid RequestId, ServiceResultStatus Status, long? Revision,
    JsonElement? Payload, ServiceError? Error) : ServiceMessage;
/// <summary>Requests cancellation of an admitted operation.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="RequestId">The originating request identity.</param>
public sealed record CancelMessage(Guid Epoch, Guid RequestId) : ServiceMessage;
/// <summary>Publishes a named service notification.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="Event">The notification name.</param>
/// <param name="Payload">The operation-specific JSON payload.</param>
public sealed record EventMessage(Guid Epoch, string Event, JsonElement Payload) : ServiceMessage;

/// <summary>A callback must name either an active originating request or an admitted background owner lease.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="CallbackId">The callback identity used to correlate its result.</param>
/// <param name="RequestId">The originating request identity.</param>
/// <param name="OwnerLeaseId">The admitted background owner lease when no request owns the callback.</param>
/// <param name="ContextRevision">The server-registered immutable authority context revision.</param>
/// <param name="Operation">The named operation to dispatch.</param>
/// <param name="DeadlineMilliseconds">The operation deadline duration in milliseconds.</param>
/// <param name="Payload">The operation-specific JSON payload.</param>
public sealed record CapabilityRequestMessage(Guid Epoch, Guid CallbackId, Guid? RequestId, Guid? OwnerLeaseId,
    long ContextRevision, string Operation, int DeadlineMilliseconds, JsonElement Payload) : ServiceMessage;
/// <summary>Returns a subscriber capability callback result.</summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="CallbackId">The callback identity used to correlate its result.</param>
/// <param name="Status">The operation’s completion or cancellation status.</param>
/// <param name="Payload">The operation-specific JSON payload.</param>
/// <param name="Error">The safe error, when the operation fails.</param>
public sealed record CapabilityResultMessage(Guid Epoch, Guid CallbackId, ServiceResultStatus Status,
    JsonElement? Payload, ServiceError? Error) : ServiceMessage;

/// <summary>
/// One body chunk, carried in a binary frame with the raw bytes after its header. Transfer IDs belong to one
/// connection and request. FinalSha256 is set only on the final chunk.
/// </summary>
/// <param name="Epoch">The service process epoch that owns this exchange.</param>
/// <param name="RequestId">The originating request identity.</param>
/// <param name="TransferId">The connection-local body transfer identity.</param>
/// <param name="Sequence">The ordered chunk sequence number.</param>
/// <param name="TotalBytes">The complete transfer length in bytes.</param>
/// <param name="Data">The chunk bytes.</param>
/// <param name="FinalSha256">The 32 raw SHA-256 bytes of the complete body on the final chunk, and null otherwise.</param>
public sealed record BlobChunkMessage(Guid Epoch, Guid RequestId, Guid TransferId, int Sequence, long TotalBytes,
    byte[] Data, byte[]? FinalSha256) : ServiceMessage;

/// <summary>A service failure: its kind, its identity and, when a handler threw, its reason.</summary>
/// <param name="Code">The error classification.</param>
/// <param name="CorrelationId">The identity used to correlate diagnostics.</param>
/// <param name="Message">
/// The failure's type in parentheses and its message when a service or callback handler threw; null otherwise. A handler
/// keeps secrets out of its exception messages, so the text carries none.
/// </param>
public sealed record ServiceError(ServiceErrorCode Code, Guid CorrelationId, string? Message = null);
