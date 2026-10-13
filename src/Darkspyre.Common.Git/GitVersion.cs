using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>The one Git version gate.</summary>
public static class GitVersion
{
    // The version read keeps only Git's version line; anything longer is not an answer.
    private const int OutputLimit = 64 * 1024;

    /// <summary>The oldest Git the library accepts.</summary>
    public static Version Minimum { get; } = new(2, 34);

    /// <summary>
    /// Runs <c>git --version</c> in <paramref name="workingDirectory"/> through <paramref name="processes"/>, waiting at
    /// most 5 seconds and keeping at most 64 KiB of its output, and reads the answer with <see cref="Parse"/>.
    /// </summary>
    public static async Task<GitCapabilities> ReadAsync(IGitProcessLauncher processes, string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            GitProcessStart start = new(["--version"], workingDirectory, ImmutableDictionary<string, string?>.Empty, GitProcessKind.Read);
            IGitProcess process = await processes.StartAsync(start, deadline.Token).ConfigureAwait(false);
            GitRunResult result;
            await using (process.ConfigureAwait(false))
                result = await GitCapture.CaptureAsync(process, new GitRunOptions { OutputLimit = OutputLimit }, deadline.Token)
                    .ConfigureAwait(false);
            if (result.OutputLimitExceeded || result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null)
                return new(false, "The installed Git could not report its version.");
            return Parse(Encoding.UTF8.GetString(result.Output));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "The installed Git version probe timed out.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            // No executable stderr or environment paths in shared capability diagnostics.
            return new(false, "Git is unavailable through the host process provider.");
        }
    }

    /// <summary>Reads <c>git --version</c> output: Git 2.34 or newer is available; any suffix after the number is ignored.</summary>
    public static GitCapabilities Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        const string prefix = "git version ";
        if (!output.StartsWith(prefix, StringComparison.Ordinal)) return new(false, "Git returned an unrecognized version.");
        string number = new(output[prefix.Length..].TakeWhile(character => char.IsAsciiDigit(character) || character == '.').ToArray());
        if (!Version.TryParse(number.TrimEnd('.'), out Version? version)) return new(false, "Git returned an unrecognized version.");
        return version >= Minimum ? new(true) : new(false, $"Git {version} is installed; the Git tools need Git 2.34 or newer.");
    }
}

/// <summary>Whether the installed Git is usable: Git 2.34 or newer answered its version.</summary>
/// <param name="IsAvailable">True when Git 2.34 or newer answered its version.</param>
/// <param name="Diagnostic">Why Git is unusable, or null.</param>
public sealed record GitCapabilities(bool IsAvailable, string? Diagnostic = null);
