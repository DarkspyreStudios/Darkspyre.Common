using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// The host's Git workflow reads against real repositories through the plain test launcher: ancestry of two revisions,
/// and status with each kind of operation in progress and with an upstream that no longer exists.
/// </summary>
public sealed class GitWorkflowReadTests
{
    /// <summary>G10.</summary>
    [Fact]
    public async Task AncestryReportsMergeBasesCountsAndContainment()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-ancestry-");
        GitConversation git = repository.First;
        string root = repository.Head();
        repository.Run("checkout", "-q", "-b", "feature");
        repository.Write("f.txt", "one\n");
        repository.Commit("Feature one");
        repository.Write("f.txt", "two\n");
        string feature = repository.Commit("Feature two");
        repository.Run("checkout", "-q", "main");
        repository.Write("m.txt", "main\n");
        string main = repository.Commit("Main");
        repository.Run("checkout", "-q", "--orphan", "lonely");
        repository.Run("rm", "-q", "-r", "-f", ".");
        repository.Write("l.txt", "lonely\n");
        string lonely = repository.Commit("Lonely");

        GitAncestryResult diverged = await git.Reader.ReadAncestryAsync(new(repository.Path, "main", "feature"), default);
        GitAncestryResult forward = await git.Reader.ReadAncestryAsync(new(repository.Path, root, "feature"), default);
        GitAncestryResult unrelated = await git.Reader.ReadAncestryAsync(new(repository.Path, "main", "lonely"), default);

        Assert.Equal(new GitAncestryResult(main, feature, diverged.MergeBases, 1, 2, false, false), diverged);
        Assert.Equal(root, Assert.Single(diverged.MergeBases));
        Assert.Equal((0, 2, false, true), (forward.LeftAhead, forward.RightAhead, forward.LeftContainsRight, forward.RightContainsLeft));
        Assert.Equal(root, Assert.Single(forward.MergeBases));
        Assert.Equal((main, lonely), (unrelated.ResolvedLeft, unrelated.ResolvedRight));
        Assert.Empty(unrelated.MergeBases);
        Assert.False(unrelated.LeftContainsRight || unrelated.RightContainsLeft);
    }

    /// <summary>G12: each operation's kind and conflicted paths.</summary>
    [Theory]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("merge")]
    [InlineData("revert")]
    public async Task StatusReportsAConflictedOperationWithItsKindAndPaths(string kind)
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-status-" + kind + "-");
        GitConversation git = repository.First;
        repository.Run("checkout", "-q", "-b", "feature");
        repository.Write("a.txt", "feature\n");
        string feature = repository.Commit("Feature");
        repository.Run("checkout", "-q", "main");
        repository.Write("a.txt", "main\n");
        string main = repository.Commit("Main");
        int exit = kind switch
        {
            "rebase" => repository.TryRun("rebase", "--onto", "feature", "main~1", "main"),
            "cherry-pick" => repository.TryRun("cherry-pick", feature),
            "merge" => repository.TryRun("merge", "--no-edit", "feature"),
            _ => Revert(repository, main),
        };
        Assert.NotEqual(0, exit);

        GitStatusSnapshot status = (await git.StatusAsync(repository.Path))!;

        GitOperationState operation = Assert.IsType<GitOperationState>(status.Operation);
        Assert.Equal(kind, operation.Kind);
        Assert.Equal("a.txt", Assert.Single(operation.ConflictedPaths));
        Assert.Contains(status.Changes, change => change.Path == "a.txt" && change.IndexStatus != '.');
    }

    /// <summary>G12: an upstream that no longer exists locally has no commit and no counts; a present one has both.</summary>
    [Fact]
    public async Task StatusReportsAGoneUpstreamWithNoCommitOrCounts()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-status-upstream-");
        GitConversation git = repository.First;
        string pushed = repository.Head();
        repository.Write("b.txt", "ahead\n");
        string head = repository.Commit("Ahead");

        GitStatusSnapshot tracking = (await git.StatusAsync(repository.Path))!;
        repository.Run("push", "-q", "origin", "main:refs/heads/topic");
        repository.Run("checkout", "-q", "-b", "topic", "--track", "origin/topic");
        repository.Run("update-ref", "-d", "refs/remotes/origin/topic");
        GitStatusSnapshot gone = (await git.StatusAsync(repository.Path))!;

        Assert.Equal((head, "refs/heads/main"), (tracking.Head, tracking.BranchRef));
        Assert.Equal(new GitUpstream("refs/remotes/origin/main", pushed, 1, 0), tracking.Upstream);
        Assert.Null(tracking.Operation);
        Assert.Equal("refs/heads/topic", gone.BranchRef);
        Assert.Equal(new GitUpstream("refs/remotes/origin/topic", null, null, null), gone.Upstream);
    }

    /// <summary>
    /// Status and diff leave out a path the path policy refuses, with its name and changes; a history filter on a
    /// refused path is refused before Git reads the history.
    /// </summary>
    [Fact]
    public async Task ReadsLeaveOutPathsTheScopeRefuses()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-read-scope-");
        GitConversation git = repository.First;
        repository.Write("secret/hidden.txt", "hidden\n");
        repository.Write("open.txt", "open\n");
        repository.Commit("Files");
        repository.Write("secret/hidden.txt", "hidden changed\n");
        repository.Write("open.txt", "open changed\n");
        string refused = Path.Combine(repository.Path, "secret");
        repository.Paths.Refuse = path => path.StartsWith(refused, StringComparison.Ordinal);

        GitStatusSnapshot status = (await git.StatusAsync(repository.Path))!;
        string patch;
        IReadOnlyList<GitChangedFile> files;
        await using (GitDiffResult diff = await git.Reader.ReadDiffAsync(new(repository.Path, new IndexToWorktree())))
        {
            files = diff.Files;
            using StreamReader reader = new(diff.Patch);
            patch = await reader.ReadToEndAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            git.Reader.ReadHistoryAsync(new(repository.Path, "HEAD", ["secret/hidden.txt"], 10), default));

        Assert.Equal(["open.txt"], status.Changes.Select(change => change.Path));
        Assert.Equal(["open.txt"], files.Select(file => file.Path));
        Assert.Contains("open changed", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", patch, StringComparison.Ordinal);
        Assert.Equal(0, git.Launcher.Starts("log"));
    }

    // Reverting the main commit after another change to the same line conflicts.
    private static int Revert(GitWorkflowRepository repository, string main)
    {
        repository.Write("a.txt", "later\n");
        repository.Commit("Later");
        return repository.TryRun("revert", "--no-edit", main);
    }
}
