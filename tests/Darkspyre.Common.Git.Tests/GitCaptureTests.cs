using System.Collections.Immutable;
using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>The library's one run-to-completion capture of a Git child.</summary>
public sealed class GitCaptureTests
{
    /// <summary>
    /// M16: a child that writes more than the output bound is drained to its exit, so it never blocks on a full pipe, and
    /// the result keeps at most the bound and reports that it was exceeded.
    /// </summary>
    [Fact]
    public async Task ACaptureOverItsOutputLimitDrainsToTheExitAndReportsIt()
    {
        const int limit = 64 * 1024;
        await using GitTestRepository repository = GitTestRepository.Create("git-capture-limit-");
        byte[] large = new byte[1024 * 1024];
        Random.Shared.NextBytes(large);
        repository.Write("large.bin", large);
        repository.Commit();
        string blob = repository.Run("rev-parse", "HEAD:large.bin").TrimEnd('\n');

        IGitProcess process = await new SystemGitProcessLauncher().StartAsync(new(["cat-file", "blob", blob], repository.Path,
            ImmutableDictionary<string, string?>.Empty, GitProcessKind.Read), default);
        GitRunResult result;
        await using (process)
            result = await GitCapture.CaptureAsync(process, new GitRunOptions { OutputLimit = limit, Timeout = TimeSpan.FromSeconds(30) }, default);

        Assert.Equal(0, result.Exit.ExitCode);
        Assert.False(result.TimedOut);
        Assert.True(result.OutputLimitExceeded);
        Assert.Equal(large.LongLength, result.OutputBytes);
        Assert.True(result.Output.Length + result.Errors.Length <= limit, "The capture kept more than its bound.");
    }
}
