namespace Darkspyre.Common.Git;

/// <summary>Starts the git executable. The library starts every Git child through it.</summary>
public interface IGitProcessLauncher
{
    /// <summary>
    /// Starts git with <paramref name="start"/>'s arguments in its working directory, with its environment entries set
    /// (an entry whose value is null is removed) and standard input, output and error redirected.
    /// </summary>
    Task<IGitProcess> StartAsync(GitProcessStart start, CancellationToken cancellationToken);
}

/// <summary>One Git child to start.</summary>
/// <param name="Arguments">The arguments after the executable name.</param>
/// <param name="WorkingDirectory">The absolute working directory.</param>
/// <param name="Environment">Entries to set; a null value removes the entry.</param>
/// <param name="Kind">Whether the child reads or changes the repository.</param>
public sealed record GitProcessStart(IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, GitProcessKind Kind);

/// <summary>What a Git child does, so a launcher knows whether it may keep the child's output.</summary>
public enum GitProcessKind
{
    /// <summary>A read. Its output can name files the caller may not see; a launcher keeps no copy of it.</summary>
    Read,
    /// <summary>A change to the repository. A launcher may keep a copy of its output.</summary>
    Change,
}

/// <summary>One running Git child. Disposing it stops a child that is still running.</summary>
public interface IGitProcess : IAsyncDisposable
{
    /// <summary>The child's standard input.</summary>
    Stream StandardInput { get; }
    /// <summary>The child's standard output.</summary>
    Stream StandardOutput { get; }
    /// <summary>The child's standard error.</summary>
    Stream StandardError { get; }
    /// <summary>Completes when the child exits.</summary>
    Task<GitExit> WaitForExitAsync(CancellationToken cancellationToken);
    /// <summary>Asks the child to stop. It does not wait for the exit.</summary>
    Task RequestStopAsync(CancellationToken cancellationToken);
    /// <summary>Stops the child at once.</summary>
    Task ForceStopAsync(CancellationToken cancellationToken);
}

/// <summary>How a Git child exited.</summary>
/// <param name="ExitCode">The exit code, or null when a signal ended the child.</param>
/// <param name="Diagnostic">A launcher-written reason the exit is not a plain exit code, or null.</param>
public sealed record GitExit(int? ExitCode, string? Diagnostic = null);
