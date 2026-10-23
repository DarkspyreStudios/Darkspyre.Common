using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Darkspyre.Common.Git;

/// <summary>
/// Turns a failed Git exit into the library's error. A failure the library cannot handle bubbles up with Git's own reason
/// and is never hidden: the message names the exit code and carries the <c>fatal:</c> and <c>error:</c> lines Git wrote.
/// A credential never surfaces: every URL in those lines loses its user name and password.
/// </summary>
internal static partial class GitOperationChecks
{
    // Three lines name the failure; Git can write one error: line per ref or path, which would flood a one-line diagnostic.
    private const int ReasonLines = 3;

    /// <summary>Throws <see cref="IOException"/> with the exit code and Git's reason when Git exited with a failure.</summary>
    internal static void Check(GitRunResult result, string operation)
    {
        // No retry, reset or cleanup is inferred from this failure.
        if (result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null) throw new IOException(Failure(result, operation));
    }

    /// <summary>
    /// Throws <see cref="GitOperationException"/> with <see cref="GitFailure.CommandFailed"/> when Git exited with a failure
    /// after it may have started changing the repository. The message names Git's exit code and Git's reason.
    /// </summary>
    internal static void RequireCompleted(GitRunResult result, string operation)
    {
        if (result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null)
            throw new GitOperationException(GitFailure.CommandFailed, Failure(result, operation));
    }

    /// <summary>
    /// Git's own reason for a failure: up to three of the <c>fatal:</c> and <c>error:</c> lines it wrote to standard
    /// error, joined by spaces, with the user name and password taken out of every URL; null when it wrote none.
    /// </summary>
    internal static string? Reason(byte[] errors)
    {
        string[] lines = [.. Encoding.UTF8.GetString(errors).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("fatal:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
            .Take(ReasonLines)
            .Select(line => UrlUserInfo().Replace(line, "$1"))];
        return lines.Length == 0 ? null : string.Join(' ', lines);
    }

    private static string Failure(GitRunResult result, string operation) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Could not {operation}: Git exit code {result.Exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.")
        + (Reason(result.Errors) is { } reason ? " " + reason : string.Empty);

    // A scheme, then everything up to the last @ before the host: the user name and password.
    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*://)[^/\s']*@")]
    private static partial Regex UrlUserInfo();
}
