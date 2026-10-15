namespace Darkspyre.LocalServices;

/// <summary>A host-resolved installed launch specification. It must never be constructed from model tool arguments.</summary>
/// <param name="ExecutablePath">The verified executable path.</param>
/// <param name="ExecutableSha256">The expected executable digest.</param>
/// <param name="Arguments">The explicit arguments passed to the detached process.</param>
/// <param name="UserHome">The resolved user home for the detached service.</param>
/// <param name="ScratchDirectory">The service-owned scratch directory.</param>
public sealed record ServiceLaunchSpec(string ExecutablePath, string ExecutableSha256, IReadOnlyList<string> Arguments,
    string UserHome, string ScratchDirectory);

/// <summary>The observed detached service process exit.</summary>
/// <param name="ProcessId">The detached process identity.</param>
/// <param name="ExitCode">The reported exit code when available.</param>
/// <param name="Signal">The terminating signal when the platform supplies one.</param>
public sealed record ServiceProcessExit(int ProcessId, int? ExitCode, int? Signal);

/// <summary>A detached child observation. There is no Kill or disposal operation that owns backend lifetime.</summary>
/// <param name="ProcessId">The detached process identity.</param>
/// <param name="Completion">Completes when the detached process exits.</param>
public sealed record DetachedServiceProcess(int ProcessId, Task<ServiceProcessExit> Completion);
