using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// The library's local Git mutations against real repositories through a recording launcher: staging, commits with the
/// repository's own hooks, and rebase, cherry-pick and merge with their state checks and results.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GitWorkflowChangeTests
{
    /// <summary>G3.</summary>
    [Fact]
    public async Task StagingStagesOnlyTheNamedPathsAndKeepsOtherStagedEntries()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-stage-");
        GitConversation git = repository.First;
        repository.Write("b.txt", "b\n");
        repository.Write("c.txt", "c\n");
        string head = repository.Commit("Files");
        repository.Write("a.txt", "staged earlier\n");
        repository.Run("add", "a.txt");
        repository.Write("b.txt", "b changed\n");
        repository.Write("c.txt", "c changed\n");

        GitStageResult staged = await git.Commits.StageAsync(new(repository.Path, ["b.txt"]));

        Assert.Equal(["b.txt"], staged.StagedPaths);
        Assert.Equal(["a.txt", "b.txt"], repository.Staged());
        Assert.Equal("c.txt", repository.Run("diff", "--name-only").TrimEnd('\n'));
        Assert.Equal(head, repository.Head());
    }

    /// <summary>S4: Git runs the repository's pre-commit hook in the commit's own child; the host adds no hooks folder.</summary>
    [Fact]
    public async Task ACommitRunsThePreCommitHookAndCreatesNoHooksFolder()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-hook-");
        GitConversation git = repository.First;
        string marker = Path.Combine(repository.Root, "pre-commit-ran");
        GitScripts.Write(Path.Combine(repository.Path, ".git", "hooks", "pre-commit"), $"printf '%s' \"${{TMPDIR:-/tmp}}\" > '{marker}'");
        repository.Write("a.txt", "changed\n");

        GitCommitResult committed = await git.Commits.CommitAsync(new(repository.Path, ["a.txt"], "Change"));

        Assert.True(File.Exists(marker));
        string temporary = await File.ReadAllTextAsync(marker);
        Assert.Equal(repository.Head(), committed.CommitId);
        Assert.Equal("refs/heads/main", committed.BranchRef);
        Assert.Equal(1, repository.TryRun("config", "--get", "core.hooksPath"));
        Assert.Empty(Directory.GetDirectories(temporary, "git-hooks-*"));
    }

    /// <summary>G4: <c>commit --only</c> commits the named path and keeps an unrelated staged entry staged.</summary>
    [Fact]
    public async Task ACommitCommitsOnlyTheNamedPathsAndKeepsOtherStagedEntries()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-only-");
        GitConversation git = repository.First;
        repository.Write("other.txt", "staged earlier\n");
        repository.Run("add", "other.txt");
        repository.Write("a.txt", "changed\n");

        GitCommitResult committed = await git.Commits.CommitAsync(new(repository.Path, ["a.txt"], "Change"));

        Assert.Equal(committed.CommitId, repository.Head());
        Assert.Equal("a.txt", repository.Run("diff-tree", "--no-commit-id", "--name-only", "-r", committed.CommitId).TrimEnd('\n'));
        Assert.Equal(["other.txt"], repository.Staged());
    }

    /// <summary>
    /// G5: a conflict leaves the rebase in place with its conflicted paths; staging the resolution and continuing finishes
    /// the rebase.
    /// </summary>
    [Fact]
    public async Task ARebaseConflictLeavesTheOperationAndContinueFinishesIt()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-rebase-conflict-");
        GitConversation git = repository.First;
        (string main, _) = Diverge(repository);

        GitSequenceResult stopped = await git.Sequences.RebaseAsync(new(repository.Path, GitSequenceAction.Start, "main"));
        GitStatusSnapshot conflicted = (await git.StatusAsync(repository.Path))!;

        Assert.Equal(GitSequenceState.Conflicted, stopped.State);
        Assert.Equal(["a.txt"], stopped.ConflictedPaths);
        Assert.Equal(main, stopped.Head);
        Assert.Null(stopped.BranchRef);
        Assert.Empty(stopped.NewCommits);
        Assert.Equal("rebase", conflicted.Operation?.Kind);
        Assert.Equal(["a.txt"], conflicted.Operation!.ConflictedPaths);

        repository.Write("a.txt", "resolved\n");
        GitStageResult staged = await git.Commits.StageAsync(new(repository.Path, ["a.txt"]));
        GitSequenceResult completed = await git.Sequences.RebaseAsync(new(repository.Path, GitSequenceAction.Continue, null));

        Assert.Equal(["a.txt"], staged.StagedPaths);
        Assert.Equal(GitSequenceState.Completed, completed.State);
        Assert.Equal("refs/heads/feature", completed.BranchRef);
        Assert.Equal(repository.Head(), Assert.Single(completed.NewCommits));
        Assert.Equal(main, repository.Head("HEAD~1"));
        Assert.Null((await git.StatusAsync(repository.Path))!.Operation);
    }

    /// <summary>
    /// DSA-890 B40: a conflicting merge stops in place instead of aborting; staging the resolution and continuing
    /// creates one merge commit.
    /// </summary>
    [Fact]
    public async Task AMergeConflictStopsInPlaceAndContinueCreatesTheMergeCommit()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-merge-conflict-");
        GitConversation git = repository.First;
        (string main, string feature) = Diverge(repository);

        GitSequenceResult stopped = await git.Sequences.MergeAsync(new(repository.Path, GitSequenceAction.Start, "main", "Merge main"));
        GitStatusSnapshot conflicted = (await git.StatusAsync(repository.Path))!;

        Assert.Equal(GitSequenceState.Conflicted, stopped.State);
        Assert.Equal(["a.txt"], stopped.ConflictedPaths);
        Assert.Equal(feature, stopped.Head);
        Assert.Equal("merge", conflicted.Operation?.Kind);

        repository.Write("a.txt", "resolved\n");
        await git.Commits.StageAsync(new(repository.Path, ["a.txt"]));
        GitSequenceResult completed = await git.Sequences.MergeAsync(new(repository.Path, GitSequenceAction.Continue, null, null));

        Assert.Equal(GitSequenceState.Completed, completed.State);
        Assert.Equal(repository.Head(), Assert.Single(completed.NewCommits));
        Assert.Equal((feature, main), (repository.Head("HEAD^1"), repository.Head("HEAD^2")));
        Assert.Equal("Merge main", repository.Run("log", "-1", "--format=%s").TrimEnd('\n'));
        Assert.Null((await git.StatusAsync(repository.Path))!.Operation);
    }

    /// <summary>
    /// M2: with two picks whose first is already in HEAD, the plain pick stops on it, the library skips it with
    /// <c>cherry-pick --skip</c>, and the second pick completes; no pick passes <c>--empty</c>.
    /// </summary>
    [Fact]
    public async Task ACherryPickSkipsAnAlreadyAppliedCommitWithoutEmptyDrop()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-pick-plain-");
        GitConversation git = repository.First;
        (string applied, string fresh) = Picks(repository);
        string head = repository.Head();

        GitSequenceResult picked = await git.Sequences.CherryPickAsync(new(repository.Path, GitSequenceAction.Start, [applied, fresh]));

        Assert.Equal(GitSequenceState.Completed, picked.State);
        Assert.Equal([applied], picked.SkippedCommits);
        Assert.Equal(repository.Head(), Assert.Single(picked.NewCommits));
        Assert.Equal(head, repository.Head("HEAD~1"));
        Assert.Equal(1, git.Launcher.Starts("--skip"));
        Assert.DoesNotContain(git.Launcher.Requests, start => start.Arguments.Any(argument => argument.StartsWith("--empty", StringComparison.Ordinal)));
        Assert.Null((await git.StatusAsync(repository.Path))!.Operation);
    }

    // Main and feature both change a.txt from the base; the repository is left on feature. Returns both heads.
    private static (string Main, string Feature) Diverge(GitWorkflowRepository repository)
    {
        repository.Write("a.txt", "main\n");
        string main = repository.Commit("Main");
        repository.Run("checkout", "-q", "-b", "feature", "main~1");
        repository.Write("a.txt", "feature\n");
        return (main, repository.Commit("Feature"));
    }

    // A side branch holds a commit main already has as a cherry-pick, and a fresh one; the repository is left on main.
    private static (string Applied, string Fresh) Picks(GitWorkflowRepository repository)
    {
        repository.Run("checkout", "-q", "-b", "side");
        repository.Write("applied.txt", "applied\n");
        string applied = repository.Commit("Applied");
        repository.Write("fresh.txt", "fresh\n");
        string fresh = repository.Commit("Fresh");
        repository.Run("checkout", "-q", "main");
        repository.Run("cherry-pick", applied);
        return (applied, fresh);
    }
}
