using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>A complete terminal reply; an interrupted exchange never supplies a successful partial body.</summary>
/// <param name="Status">The request outcome.</param>
/// <param name="Revision">The resulting revision when the operation supplies one.</param>
/// <param name="Body">The operation-specific result body, when present.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceCallResult(ServiceResultStatus Status, long? Revision, byte[]? Body, ServiceError? Error);

