using System.Globalization;

namespace Darkspyre.Common.Git;

/// <summary>Turns a failed Git exit into the library's error, naming only the exit code.</summary>
internal static class GitOperationChecks
{
    internal static void Check(GitRunResult result, string operation)
    {
        // Raw stderr can contain credential-bearing URLs or arbitrary repository configuration.
        // No retry, reset or cleanup is inferred from this failure.
        if (result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null)
            throw new IOException($"Could not {operation}: Git exit code {result.Exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
    }

    /// <summary>
    /// Throws <see cref="GitOperationException"/> with <see cref="GitFailure.CommandFailed"/> when Git exited with a failure
    /// after it may have started changing the repository. The message names Git's exit code, never its output.
    /// </summary>
    internal static void RequireCompleted(GitRunResult result, string operation)
    {
        if (result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null)
            throw new GitOperationException(GitFailure.CommandFailed, string.Create(CultureInfo.InvariantCulture,
                $"Could not {operation}: Git exit code {result.Exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}."));
    }
}
