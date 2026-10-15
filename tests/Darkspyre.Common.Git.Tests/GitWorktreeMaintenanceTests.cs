using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>DSA-1028: locking, unlocking, pruning and attaching linked worktrees against the real Git.</summary>
public sealed class GitWorktreeMaintenanceTests
{
    /// <summary>A lock keeps Git from removing the worktree; an unlock lets it, and both leave an unchanged state alone.</summary>
    [Fact]
    public async Task ALockedWorktreeIsRemovedOnlyAfterItIsUnlocked()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-lock-");
        GitWorktrees worktrees = repository.First.Worktrees;
        string linked = Path.Combine(repository.Root, "linked");
        repository.Run("worktree", "add", "-q", "-b", "feature", linked);

        await worktrees.LockAsync(linked, "first reason");
        await worktrees.LockAsync(linked, "second reason");

        Assert.Contains("locked first reason", repository.Run("worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.NotEqual(0, repository.TryRun("worktree", "remove", linked));

        await worktrees.UnlockAsync(linked);
        await worktrees.UnlockAsync(linked);

        Assert.DoesNotContain("locked", repository.Run("worktree", "list", "--porcelain"), StringComparison.Ordinal);
        Assert.Equal(GitRemovalStatus.Removed, (await worktrees.RemoveAsync(linked)).Status);
    }

    /// <summary>
    /// An attach checks out the local branch when it exists, else starts a new branch at origin's, else at the base; a
    /// prune first clears the record of a worktree whose folder was deleted, so its branch can be checked out again.
    /// </summary>
    [Fact]
    public async Task AnAttachUsesTheLocalBranchThenOriginsThenTheBase()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-attach-");
        GitWorktrees worktrees = repository.First.Worktrees;
        string baseCommit = repository.Head();
        string removed = Path.Combine(repository.Root, "removed");
        repository.Run("worktree", "add", "-q", "-b", "local", removed);
        Directory.Delete(removed, recursive: true);
        repository.Write("remote.txt", "remote\n");
        string remoteCommit = repository.Commit("Remote");
        repository.Run("push", "-q", "origin", "HEAD:refs/heads/published");
        repository.Run("reset", "-q", "--hard", baseCommit);
        repository.Run("fetch", "-q", "origin");

        await worktrees.PruneAsync(repository.Path);
        GitWorktreeAttachResult local = await worktrees.AttachAsync(new(repository.Path, removed, "local", "main"));
        GitWorktreeAttachResult remote = await worktrees.AttachAsync(new(repository.Path, Path.Combine(repository.Root, "published"),
            "published", "main"));
        GitWorktreeAttachResult created = await worktrees.AttachAsync(new(repository.Path, Path.Combine(repository.Root, "fresh"),
            "fresh", "main"));

        Assert.Equal(GitWorktreeAttachSource.LocalBranch, local.Source);
        Assert.Equal("refs/heads/local", local.Worktree.BranchRef);
        Assert.Equal(GitWorktreeAttachSource.RemoteBranch, remote.Source);
        Assert.Equal(remoteCommit, remote.Worktree.BaseCommit);
        Assert.Equal(GitWorktreeAttachSource.NewBranch, created.Source);
        Assert.Equal(baseCommit, created.Worktree.BaseCommit);
        Assert.Equal("refs/heads/fresh", created.Worktree.BranchRef);
    }
}
