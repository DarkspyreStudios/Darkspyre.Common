using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// DSA-890 B40: stash apply, path restore and branch delete against real repositories through a recording launcher.
/// </summary>
public sealed class GitLocalChangesTests
{
    /// <summary>A conflicting apply reports the conflicted path and keeps the stash.</summary>
    [Fact]
    public async Task AConflictingStashApplyKeepsTheStash()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-stash-conflict-");
        GitConversation git = repository.First;
        repository.Write("a.txt", "stashed\n");
        GitStashSaveResult saved = await git.Local.SaveStashAsync(new(repository.Path, "Work in progress", false));
        repository.Write("a.txt", "committed\n");
        repository.Commit("Conflicting");

        GitStashApplyResult applied = await git.Local.ApplyStashAsync(new(repository.Path, saved.StashCommit!));

        Assert.Equal("stash@{0}", saved.StashRef);
        Assert.Equal(GitSequenceState.Conflicted, applied.State);
        Assert.Equal(["a.txt"], applied.ConflictedPaths);
        GitStashEntry kept = Assert.Single(await git.Local.ListStashesAsync(repository.Path));
        Assert.Equal((0, saved.StashCommit, "Work in progress", "main"), (kept.Index, kept.StashCommit, kept.Message, kept.Branch));
    }

    /// <summary>A restored path changes in the worktree only; the index keeps what it had.</summary>
    [Fact]
    public async Task ARestoreWritesTheWorktreeWithoutStaging()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-restore-unstaged-");
        GitConversation git = repository.First;
        string original = repository.Head();
        repository.Write("a.txt", "changed\n");
        repository.Commit("Change");

        GitRestoreResult restored = await git.Local.RestorePathsAsync(new(repository.Path, original, ["a.txt"]));

        Assert.Equal(original, restored.CommitId);
        Assert.Equal(["a.txt"], restored.RestoredPaths);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
        Assert.Empty(repository.Staged());
        Assert.Equal("a.txt", repository.Run("diff", "--name-only").TrimEnd('\n'));
    }

    /// <summary>A path absent at the revision is reported missing and the present paths are still restored.</summary>
    [Fact]
    public async Task ARestoreWithAMissingPathRestoresTheOthers()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-restore-missing-");
        GitConversation git = repository.First;
        string original = repository.Head();
        repository.Write("a.txt", "changed\n");
        repository.Write("new.txt", "new\n");
        repository.Commit("Change and add");

        GitRestoreResult restored = await git.Local.RestorePathsAsync(new(repository.Path, original, ["new.txt", "a.txt"]));

        Assert.Equal(["a.txt"], restored.RestoredPaths);
        Assert.Equal(["new.txt"], restored.MissingPaths);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repository.Path, "new.txt")));
    }

    /// <summary>A branch that moved since the caller read it is not deleted.</summary>
    [Fact]
    public async Task ABranchThatMovedIsNotDeleted()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-branch-stale-");
        GitConversation git = repository.First;
        string seen = repository.Head();
        repository.Run("branch", "topic");
        repository.Run("checkout", "-q", "topic");
        string moved = repository.Commit("Moved");
        repository.Run("checkout", "-q", "main");

        GitOperationException stale = await Assert.ThrowsAsync<GitOperationException>(() =>
            git.Local.DeleteBranchAsync(new(repository.Path, "topic", seen)));
        GitBranchResult deleted = await git.Local.DeleteBranchAsync(new(repository.Path, "topic", moved));

        Assert.Equal(GitFailure.StaleState, stale.Failure);
        Assert.Equal(("refs/heads/topic", moved), (deleted.BranchRef, deleted.CommitId));
        Assert.Equal(1, repository.TryRun("show-ref", "--verify", "--quiet", "refs/heads/topic"));
    }

    /// <summary>
    /// M7: branch delete and worktree reset read the checked-out worktrees from the plain <c>worktree list --porcelain</c>
    /// as a read: a branch checked out in a linked worktree is neither deleted nor switched to, and a free branch is.
    /// </summary>
    [Fact]
    public async Task BranchDeleteAndResetSeeTheCheckedOutWorktreeFromThePlainList()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-worktree-list-");
        GitConversation git = repository.First;
        string head = repository.Head();
        string topic = Path.Combine(repository.Root, "topic tree");
        string other = Path.Combine(repository.Root, "other");
        repository.Run("branch", "topic");
        repository.Run("branch", "free");
        repository.Run("worktree", "add", "-q", topic, "topic");
        repository.Run("worktree", "add", "-q", "-b", "other", other);
        GitWorkspaceTransitions transitions = new(git.Commands, git.Reader, repository.Locks, repository.Paths);

        GitOperationException checkedOut = await Assert.ThrowsAsync<GitOperationException>(() =>
            git.Local.DeleteBranchAsync(new(repository.Path, "topic", head)));
        GitResetResult occupied = await transitions.ResetAsync(new(other, head, new GitResetToExistingBranch("topic")));
        GitResetResult switched = await transitions.ResetAsync(new(other, head, new GitResetToExistingBranch("free")));

        Assert.Equal(GitFailure.BranchCheckedOut, checkedOut.Failure);
        Assert.Contains(topic, checkedOut.Message, StringComparison.Ordinal);
        Assert.Equal(GitResetStatus.Refused, occupied.Status);
        Assert.Equal("The target branch is checked out in another worktree.", occupied.Diagnostic);
        Assert.Equal((GitResetStatus.Reset, "refs/heads/free"), (switched.Status, switched.BranchRef));
        Assert.Equal(head, repository.Head("refs/heads/topic"));
        Assert.All(git.Launcher.Requests.Where(start => start.Arguments.Contains("worktree")),
            start => Assert.Equal(GitProcessKind.Read, start.Kind));
    }
}
