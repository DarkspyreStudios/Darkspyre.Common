namespace Darkspyre.Common.Git;

/// <summary>One Git command line to run exactly as given.</summary>
/// <param name="WorkingDirectory">The absolute folder Git starts in.</param>
/// <param name="Arguments">The arguments after <c>git</c>, passed unchanged: no option is added.</param>
/// <param name="Timeout">How long Git may run before it is asked to stop. It must be positive.</param>
/// <param name="OutputLimit">The most bytes kept across both output channels. It must not be negative.</param>
public sealed record GitCommandLineRequest(string WorkingDirectory, IReadOnlyList<string> Arguments, TimeSpan Timeout,
    int OutputLimit);

/// <summary>How one command line ended and what Git wrote.</summary>
/// <param name="Exit">How Git exited.</param>
/// <param name="Output">Standard output, within the request's output limit.</param>
/// <param name="Errors">Standard error, within the request's output limit.</param>
/// <param name="TimedOut">True when the timeout stopped Git.</param>
/// <param name="OutputBytes">Every byte Git wrote to both channels, kept or not.</param>
public sealed record GitCommandLineResult(GitExit Exit, byte[] Output, byte[] Errors, bool TimedOut, long OutputBytes);

/// <summary>Runs one Git command line as a terminal would, with standard input closed and nothing that waits for a person.</summary>
public static class GitCommandLine
{
    /// <summary>
    /// Starts <c>git</c> with the request's arguments through <paramref name="processes"/> as a
    /// <see cref="GitProcessKind.Change"/> child and captures it through the library's one capture path.
    /// </summary>
    /// <gotcha>It checks no path: the caller confines the working directory and any folder the arguments name. Git
    /// starts no editor and asks for no credential: the editor default is <c>false</c>, so a command that needs one fails at
    /// once with Git's message, and a <c>-c core.editor=...</c> in the arguments still wins.</gotcha>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the child is stopped
    /// when its process is disposed.</exception>
    public static async Task<GitCommandLineResult> RunAsync(IGitProcessLauncher processes, GitCommandLineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        if (!Path.IsPathFullyQualified(request.WorkingDirectory))
            throw new ArgumentException("The working directory must be absolute.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Arguments);
        if (request.Arguments.Count == 0) throw new ArgumentException("At least one Git argument is required.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(request.OutputLimit);
        GitRunResult result = await new GitCommands(processes).RunCommandLineAsync(request.WorkingDirectory,
            request.Arguments, request.Timeout, request.OutputLimit, cancellationToken).ConfigureAwait(false);
        return new(result.Exit, result.Output, result.Errors, result.TimedOut, result.OutputBytes);
    }
}
