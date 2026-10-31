namespace Darkspyre.LocalServices.Contracts;

/// <summary>One call's wait for its turn on its connection, summed over the call's own frames.</summary>
/// <param name="Operation">The operation that waited.</param>
/// <param name="RequestClass">The class the call was admitted under.</param>
/// <param name="Waited">How long the call's own frames waited for the connection's outbound turn, in total.</param>
/// <gotcha>The wait is the caller's own measurement and reaches the caller without the service answering anything, so
/// it is recorded even while the connection the call is on is stalled.</gotcha>
public sealed record ServiceCallWait(string Operation, ServiceRequestClass RequestClass, TimeSpan Waited);
