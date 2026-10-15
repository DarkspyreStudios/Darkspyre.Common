namespace Darkspyre.LocalServices;

/// <summary>The result of family-wide launch admission.</summary>
public enum ServiceLaunchDecision
{
    /// <summary>This caller reserves a launch attempt.</summary>
    Reserved,
    /// <summary>An existing service owns the family lock.</summary>
    OwnerPresent,
    /// <summary>Another launch attempt is still pending.</summary>
    Starting,
    /// <summary>The launch window has no remaining attempts.</summary>
    Exhausted,
}
/// <summary>The launch decision and reserved attempt identity when admitted.</summary>
/// <param name="Decision">The launch admission result.</param>
/// <param name="AttemptId">The reserved launch attempt identity.</param>
public sealed record ServiceLaunchReservation(ServiceLaunchDecision Decision, Guid? AttemptId = null);

