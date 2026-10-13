using System.Text;
using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>Local inventory distinguishes authoritative removal from an unavailable read.</summary>
public sealed class GitInventoryTests
{
    /// <summary>Removing a worktree leaves its branch until an explicit prune.</summary>
    [Fact]
    public async Task LinkedAndDetachedWorktreesRemainSeparateFromRetainedBranches()
    {
        await using GitTestRepository repository = GitTestRepository.Create("inventory");
        string commit = repository.Commit();
        string linked = Path.Combine(repository.Root, "linked");
        string detached = Path.Combine(repository.Root, "detached");
        repository.Run("worktree", "add", "-q", "-b", "topic", linked);
        repository.Run("worktree", "add", "-q", "--detach", detached);
        GitClient client = new(repository.Launcher, repository.Paths, new());
        IReadOnlyList<GitWorktreeEntry> worktrees = await client.ReadWorktreesAsync(repository.Path);
        Assert.Equal(3, worktrees.Count);
        Assert.Contains(worktrees, w => w.Path == linked && w.BranchRef == "refs/heads/topic" && w.HeadCommit == commit);
        Assert.Contains(worktrees, w => w.Path == detached && w.BranchRef is null && w.HeadCommit == commit);
        repository.Run("worktree", "remove", linked);
        Assert.DoesNotContain(await client.ReadWorktreesAsync(repository.Path), w => w.Path == linked);
        Assert.Contains(await client.ReadLocalBranchesAsync(repository.Path), b => b.FullRefName == "refs/heads/topic");
        repository.Run("branch", "-D", "topic");
        GitBranchEntry branch = Assert.Single(await client.ReadLocalBranchesAsync(repository.Path));
        Assert.Equal(new("refs/heads/main", commit), branch);
        Assert.All(repository.Launcher.Requests, start => Assert.Equal(GitProcessKind.Read, start.Kind));
    }

    /// <summary>An unavailable location remains registered while path-policy refusals stay private.</summary>
    [Fact]
    public async Task MissingWorktreeRemainsListedAndDeniedLocationsAreOmitted()
    {
        await using GitTestRepository repository = GitTestRepository.Create("inventory-missing");
        repository.Commit();
        string linked = Path.Combine(repository.Root, "linked");
        repository.Run("worktree", "add", "-q", "-b", "topic", linked);
        Directory.Delete(linked, recursive: true);
        GitClient client = new(repository.Launcher, repository.Paths, new());
        Assert.Contains(await client.ReadWorktreesAsync(repository.Path), w => w.Path == linked);
        repository.Paths.Refuse = path => path == linked;
        Assert.DoesNotContain(await client.ReadWorktreesAsync(repository.Path), w => w.Path == linked);
        repository.Paths.Refuse = _ => true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ReadLocalBranchesAsync(repository.Path));
    }

    /// <summary>An unborn branch is empty; an unavailable repository is a failed observation.</summary>
    [Fact]
    public async Task UnbornBranchHasNoCommittedRefAndUnavailableRepositoryThrows()
    {
        await using GitTestRepository repository = GitTestRepository.Create("inventory-unborn");
        GitClient client = new(repository.Launcher, repository.Paths, new());
        GitWorktreeEntry worktree = Assert.Single(await client.ReadWorktreesAsync(repository.Path));
        Assert.Null(worktree.HeadCommit);
        Assert.Equal("refs/heads/main", worktree.BranchRef);
        Assert.Empty(await client.ReadLocalBranchesAsync(repository.Path));
        Directory.Delete(repository.Path, recursive: true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => client.ReadLocalBranchesAsync(repository.Path));
    }

    /// <summary>A broken ref diagnostic prevents a false deletion observation.</summary>
    [Fact]
    public async Task BrokenRefCannotLookLikePrunedBranch()
    {
        await using GitTestRepository repository = GitTestRepository.Create("inventory-broken-ref");
        repository.Commit();
        repository.Run("branch", "topic");
        await File.WriteAllTextAsync(Path.Combine(repository.Path, ".git", "refs", "heads", "topic"), "broken\n");
        GitClient client = new(repository.Launcher, repository.Paths, new());
        await Assert.ThrowsAsync<IOException>(() => client.ReadLocalBranchesAsync(repository.Path));
    }

    /// <summary>Malformed output never supplies an authoritative inventory.</summary>
    [Theory]
    [InlineData("worktree /repo\n")]
    [InlineData("worktree /repo\nHEAD bad\n\n")]
    [InlineData("worktree /repo\nHEAD 0000000000000000000000000000000000000000")]
    public void IncompleteInventoryNeverMeansRemoval(string output) =>
        Assert.Throws<InvalidDataException>(() => GitWorktreeList.Parse(Encoding.UTF8.GetBytes(output)));
}
