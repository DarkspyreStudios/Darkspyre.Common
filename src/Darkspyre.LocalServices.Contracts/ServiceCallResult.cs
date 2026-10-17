using System.Text.Json;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>A complete terminal reply; an interrupted exchange never supplies a successful partial body.</summary>
/// <param name="Status">The request outcome.</param>
/// <param name="Revision">The resulting revision when the operation supplies one.</param>
/// <param name="Body">The operation-specific result body, when present.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceCallResult(ServiceResultStatus Status, long? Revision, byte[]? Body, ServiceError? Error);


/// <summary>A subscriber's answer to a service callback.</summary>
/// <param name="Status">The callback outcome.</param>
/// <param name="Payload">The operation-specific result, when present.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceCallbackResult(ServiceResultStatus Status, JsonElement? Payload = null, ServiceError? Error = null);

/// <summary>Runs a service callback on a subscriber. The token is cancelled by the service's Cancel, the callback's
/// deadline or the subscription closing.</summary>
public delegate ValueTask<ServiceCallbackResult> ServiceCallbackHandler(CapabilityRequestMessage request, CancellationToken cancellationToken);
