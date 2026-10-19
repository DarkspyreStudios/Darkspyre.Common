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

    /// <summary>A pre-commit hook that refuses fails the commit as Git's failure and leaves HEAD where it was.</summary>
    [Fact]
    public async Task ARefusingPreCommitHookFailsWithCommandFailedAndKeepsHead()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-refused-");
        GitConversation git = repository.First;
        string head = repository.Head();
        GitScripts.Write(Path.Combine(repository.Path, ".git", "hooks", "pre-commit"), "exit 1");
        repository.Write("a.txt", "changed\n");

        GitOperationException failed = await Assert.ThrowsAsync<GitOperationException>(() =>
            git.Commits.CommitAsync(new(repository.Path, ["a.txt"], "Change")));

        Assert.Equal(GitFailure.CommandFailed, failed.Failure);
        Assert.Equal(head, repository.Head());
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

    /// <summary>A deletion already staged, as <c>git rm</c> or an earlier stage leaves it, is committed with the other named paths.</summary>
    [Fact]
    public async Task ACommitIncludesAPathWhoseDeletionIsAlreadyStaged()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-staged-deletion-");
        GitConversation git = repository.First;
        repository.Write("b.txt", "b\n");
        repository.Commit("Second file");
        repository.Run("rm", "-q", "a.txt");
        repository.Write("b.txt", "b changed\n");

        GitCommitResult committed = await git.Commits.CommitAsync(new(repository.Path, ["a.txt", "b.txt"], "Change"));

        Assert.Equal(committed.CommitId, repository.Head());
        Assert.Equal("D\ta.txt\nM\tb.txt",
            repository.Run("diff-tree", "--no-commit-id", "--name-status", "-r", committed.CommitId).TrimEnd('\n'));
        Assert.Empty(repository.Staged());
    }

    /// <summary>A commit needs a checked-out branch: on a detached HEAD it is stale before anything is staged.</summary>
    [Fact]
    public async Task ACommitOnADetachedHeadIsRefusedBeforeStaging()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-detached-");
        GitConversation git = repository.First;
        string head = repository.Head();
        repository.Run("checkout", "-q", "--detach");
        repository.Write("a.txt", "changed\n");

        GitOperationException refused = await Assert.ThrowsAsync<GitOperationException>(() =>
            git.Commits.CommitAsync(new(repository.Path, ["a.txt"], "Change")));

        Assert.Equal(GitFailure.StaleState, refused.Failure);
        Assert.Empty(repository.Staged());
        Assert.Equal(head, repository.Head());
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

    /// <summary>G15.</summary>
    [Fact]
    public async Task ARebaseStartWithUnstagedChangesOrAMergeInProgressIsRefusedAsNotClean()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-rebase-clean-");
        GitConversation git = repository.First;
        Diverge(repository);
        string feature = repository.Head();
        repository.Write("a.txt", "unstaged\n");

        GitOperationException unstaged = await Assert.ThrowsAsync<GitOperationException>(() => git.Sequences.RebaseAsync(new(repository.Path, GitSequenceAction.Start, "main")));
        repository.Run("checkout", "-q", "--", "a.txt");
        Assert.NotEqual(0, repository.TryRun("merge", "--no-edit", "main"));
        GitOperationException merge = await Assert.ThrowsAsync<GitOperationException>(() => git.Sequences.RebaseAsync(new(repository.Path, GitSequenceAction.Start, "main")));

        Assert.Equal(GitFailure.NotClean, unstaged.Failure);
        Assert.Contains("a.txt", unstaged.Message, StringComparison.Ordinal);
        Assert.Equal(GitFailure.NotClean, merge.Failure);
        Assert.Equal("merge", (await git.StatusAsync(repository.Path))!.Operation?.Kind);
        Assert.Equal(feature, repository.Head());
        Assert.Equal(0, git.Launcher.Starts("rebase"));
    }

    /// <summary>G19.</summary>
    [Fact]
    public async Task ACherryPickNamesTheAlreadyAppliedCommitAsSkipped()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-pick-skip-");
        GitConversation git = repository.First;
        (string applied, string fresh) = Picks(repository);

        GitSequenceResult picked = await git.Sequences.CherryPickAsync(new(repository.Path, GitSequenceAction.Start, [fresh, applied]));

        Assert.Single(picked.NewCommits);
        Assert.Equal([applied], picked.SkippedCommits);
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

    /// <summary>
    /// M3: a pick a hook stops leaves its changes staged, so the library does not skip it: the cherry-pick stays Stopped
    /// and names no commit as skipped.
    /// </summary>
    [Fact]
    public async Task ACherryPickStoppedByAHookStaysStopped()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-pick-hook-");
        GitConversation git = repository.First;
        (_, string fresh) = Picks(repository);
        string head = repository.Head();
        GitScripts.Write(Path.Combine(repository.Path, ".git", "hooks", "prepare-commit-msg"), "exit 1");

        GitSequenceResult picked = await git.Sequences.CherryPickAsync(new(repository.Path, GitSequenceAction.Start, [fresh]));

        Assert.Equal(GitSequenceState.Stopped, picked.State);
        Assert.Empty(picked.SkippedCommits);
        Assert.Equal(0, git.Launcher.Starts("--skip"));
        Assert.Equal(head, repository.Head());
        Assert.Equal(["fresh.txt"], repository.Staged());
        Assert.Equal("cherry-pick", (await git.StatusAsync(repository.Path))!.Operation?.Kind);
    }

    /// <summary>M6: with <c>rebase.updateRefs</c> set in the repository, a rebase still moves no other branch.</summary>
    [Fact]
    public async Task ARebaseDoesNotUpdateOtherBranchRefs()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-rebase-refs-");
        GitConversation git = repository.First;
        repository.Run("config", "rebase.updateRefs", "true");
        repository.Write("b.txt", "main\n");
        repository.Commit("Main");
        repository.Run("checkout", "-q", "-b", "feature", "main~1");
        repository.Write("c.txt", "one\n");
        string middle = repository.Commit("One");
        repository.Run("branch", "middle");
        repository.Write("d.txt", "two\n");
        repository.Commit("Two");

        GitSequenceResult rebased = await git.Sequences.RebaseAsync(new(repository.Path, GitSequenceAction.Start, "main"));

        Assert.Equal(GitSequenceState.Completed, rebased.State);
        Assert.Equal(repository.Head("main"), repository.Head("HEAD~2"));
        Assert.Equal(middle, repository.Head("middle"));
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
