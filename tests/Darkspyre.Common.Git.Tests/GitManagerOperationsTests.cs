using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// DSA-1078: the File Viewer Git manager's operations through <see cref="GitClient"/>, against real repositories with a
/// bare <c>origin</c>. The only stand-in is the merge tool, a shell command, because a real editor needs a person.
/// </summary>
public sealed class GitManagerOperationsTests
{
    /// <summary>Local and origin branches come with a local branch's upstream and checked-out worktree; origin/HEAD does not.</summary>
    [Fact]
    public async Task BranchesListLocalAndOriginWithUpstreamAndWorktree()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-branches-");
        string topic = Path.Combine(repository.Root, "topic tree");
        repository.Run("remote", "set-head", "origin", "main");
        repository.Run("worktree", "add", "-q", "-b", "topic", topic);
        string head = repository.Head();

        IReadOnlyList<GitBranchReference> branches = await Client(repository).ReadBranchesAsync(repository.Path);

        Assert.Equal(
        [
            new GitBranchReference("refs/heads/main", head, "refs/remotes/origin/main", repository.Paths.Resolve(repository.Path)),
            new GitBranchReference("refs/heads/topic", head, null, repository.Paths.Resolve(topic)),
            new GitBranchReference("refs/remotes/origin/main", head, null, null),
        ], branches);
    }

    /// <summary>
    /// Checking out an origin branch creates the local branch tracking it; a switch that would overwrite a local edit is
    /// refused and the edit stays.
    /// </summary>
    [Fact]
    public async Task CheckoutOfOriginBranchTracksItAndDirtyCheckoutKeepsFiles()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-checkout-origin-");
        repository.Run("switch", "-q", "-c", "feature");
        repository.Write("a.txt", "feature\n");
        string feature = repository.Commit("Feature");
        repository.Run("push", "-q", "origin", "feature");
        repository.Run("switch", "-q", "main");
        repository.Run("branch", "-D", "feature");
        GitClient client = Client(repository);

        GitBranchResult switched = await client.CheckoutAsync(new(repository.Path, "refs/remotes/origin/feature"));
        repository.Write("a.txt", "local edit\n");
        GitOperationException dirty = await Assert.ThrowsAsync<GitOperationException>(() =>
            client.CheckoutAsync(new(repository.Path, "refs/heads/main")));

        Assert.Equal(("refs/heads/feature", feature), (switched.BranchRef, switched.CommitId));
        Assert.Equal("refs/remotes/origin/feature\n", repository.Run("for-each-ref", "--format=%(upstream)", "refs/heads/feature"));
        Assert.Equal(GitFailure.NotClean, dirty.Failure);
        Assert.Equal("refs/heads/feature\n", repository.Run("symbolic-ref", "HEAD"));
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
    }

    /// <summary>
    /// A branch checked out in another worktree is refused with that reason, named as itself or as the origin branch it
    /// tracks, and the worktree stays on its branch.
    /// </summary>
    [Fact]
    public async Task CheckoutOfBranchCheckedOutElsewhereSaysSo()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-checkout-elsewhere-");
        repository.Run("worktree", "add", "-q", "-b", "topic", Path.Combine(repository.Root, "topic tree"));
        repository.Run("push", "-q", "-u", "origin", "topic");
        GitClient client = Client(repository);

        GitOperationException local = await Assert.ThrowsAsync<GitOperationException>(() =>
            client.CheckoutAsync(new(repository.Path, "refs/heads/topic")));
        GitOperationException origin = await Assert.ThrowsAsync<GitOperationException>(() =>
            client.CheckoutAsync(new(repository.Path, "refs/remotes/origin/topic")));

        Assert.Equal((GitFailure.BranchCheckedOut, "Branch 'topic' is checked out in another worktree; nothing changed."),
            (local.Failure, local.Message));
        Assert.Equal((GitFailure.BranchCheckedOut, local.Message), (origin.Failure, origin.Message));
        Assert.Equal("refs/heads/main\n", repository.Run("symbolic-ref", "HEAD"));
    }

    /// <summary>
    /// A failure the library does not classify carries Git's own reason, here a configuration value Git cannot read, and
    /// the user name and password of the URL in that value never appear.
    /// </summary>
    [Fact]
    public async Task UnclassifiedFailureCarriesGitsReasonWithoutCredentials()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-reason-");
        repository.Write("a.txt", "staged\n");
        repository.Run("add", "a.txt");
        repository.Run("config", "commit.gpgsign", "https://user:secret@example.invalid/x");

        GitOperationException failed = await Assert.ThrowsAsync<GitOperationException>(() =>
            Client(repository).CommitStagedAsync(new(repository.Path, "Refused")));

        Assert.Equal(GitFailure.CommandFailed, failed.Failure);
        Assert.EndsWith("fatal: bad boolean config value 'https://example.invalid/x' for 'commit.gpgsign'", failed.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>Commit Staged commits the staged bytes; an edit made after staging stays an unstaged change.</summary>
    [Fact]
    public async Task CommitStagedLeavesLaterWorkingEditUnstaged()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-commit-staged-");
        repository.Write("a.txt", "staged\n");
        repository.Run("add", "a.txt");
        repository.Write("a.txt", "later\n");

        GitCommitResult commit = await Client(repository).CommitStagedAsync(new(repository.Path, "Staged only"));

        Assert.Equal((repository.Head(), "refs/heads/main"), (commit.CommitId, commit.BranchRef));
        Assert.Equal("staged\n", repository.Run("show", "HEAD:a.txt"));
        Assert.Equal("later\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
        Assert.Empty(repository.Staged());
        Assert.Equal("a.txt\n", repository.Run("diff", "--name-only"));
    }

    /// <summary>On a branch with no commits, Unstage drops the index entry of a new file whose working bytes differ, and keeps the file.</summary>
    [Fact]
    public async Task UnstageKeepsWorkingFileOnUnbornBranch()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-unstage-unborn-");
        repository.Write("new.txt", "staged\n");
        repository.Run("add", "new.txt");
        repository.Write("new.txt", "edited\n");

        GitUnstageResult unstaged = await new GitClient(repository.Launcher, repository.Paths, new GitRepositoryLocks())
            .UnstageAsync(new(repository.Path, ["new.txt"]));

        Assert.Equal(["new.txt"], unstaged.UnstagedPaths);
        Assert.Equal("", repository.Run("ls-files", "--stage"));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(repository.Path, "new.txt")));
    }

    /// <summary>The index read returns the staged bytes, not the disk's, and refuses a path that has only conflict stages.</summary>
    [Fact]
    public async Task IndexReadReturnsStagedBytesAndRefusesConflict()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-index-read-");
        GitClient client = Client(repository);
        repository.Write("a.txt", "staged\n");
        repository.Run("add", "a.txt");
        repository.Write("a.txt", "disk\n");

        string staged;
        await using (GitIndexFileContent read = await client.ReadFileAtIndexAsync(new(repository.Path, "a.txt", 1024)))
        {
            Assert.Equal((true, "100644"), (read.Present, read.Mode));
            staged = await new StreamReader(read.Content!).ReadToEndAsync();
        }
        repository.Run("reset", "-q", "--hard");
        Conflict(repository);
        GitOperationException conflicted = await Assert.ThrowsAsync<GitOperationException>(() =>
            client.ReadFileAtIndexAsync(new(repository.Path, "a.txt", 1024)));

        Assert.Equal("staged\n", staged);
        Assert.Equal(GitFailure.NotClean, conflicted.Failure);
    }

    /// <summary>Pull follows the repository's <c>pull.rebase</c>: the local commit is replayed on the fetched one, with no merge commit.</summary>
    [Fact]
    public async Task PullFollowsRepositoryRebaseSetting()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-pull-rebase-");
        string upstream = PushFromOtherClone(repository);
        repository.Write("local.txt", "local\n");
        repository.Commit("Local");
        repository.Run("config", "pull.rebase", "true");

        GitCommandResult pulled = await Client(repository).PullConfiguredAsync(repository.Path);

        Assert.Equal(new GitCommandResult(GitCommandState.Completed), pulled);
        Assert.Equal(upstream, repository.Head("HEAD^"));
        Assert.Equal(2, repository.Run("rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length);
    }

    /// <summary>A push the remote refuses because it moved on is Failed with RemoteRejected, and the remote keeps its commit.</summary>
    [Fact]
    public async Task PushReportsRemoteRejection()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-push-rejected-");
        string upstream = PushFromOtherClone(repository);
        repository.Write("local.txt", "local\n");
        repository.Commit("Local");

        GitCommandResult pushed = await Client(repository).PushConfiguredAsync(repository.Path);

        Assert.Equal((GitCommandState.Failed, GitFailure.RemoteRejected), (pushed.State, pushed.Failure));
        Assert.Equal(upstream, repository.RemoteHead("main"));
    }

    /// <summary>Resolve runs the configured merge tool on the conflicted path; afterwards the index holds the tool's result and no conflict.</summary>
    [Fact]
    public async Task ResolveRunsConfiguredToolAndRereadsIndex()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-resolve-tool-");
        Conflict(repository);
        repository.Run("config", "merge.tool", "fake");
        repository.Run("config", "mergetool.fake.cmd", "printf 'resolved\\n' > \"$MERGED\"");
        repository.Run("config", "mergetool.fake.trustExitCode", "true");
        repository.Run("config", "mergetool.keepBackup", "false");
        GitClient client = Client(repository);

        GitCommandResult resolved = await client.ResolveConflictAsync(new(repository.Path, "a.txt"));
        GitStatusSnapshot? status = await client.ReadStatusAsync(repository.Path);
        await using GitIndexFileContent index = await client.ReadFileAtIndexAsync(new(repository.Path, "a.txt", 1024));

        Assert.Equal(GitCommandState.Completed, resolved.State);
        Assert.Equal("merge", status!.Operation!.Kind);
        Assert.Empty(status.Operation.ConflictedPaths);
        Assert.Equal("resolved\n", await new StreamReader(index.Content!).ReadToEndAsync());
    }

    /// <summary>History follows a rename: the second page, read from the first page's commit, names the old path.</summary>
    [Fact]
    public async Task FileHistoryFollowsRenameAcrossPages()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-file-history-");
        repository.Write("a.txt", "change\n");
        repository.Commit("Change");
        repository.Run("mv", "a.txt", "b.txt");
        repository.Commit("Rename");
        repository.Write("b.txt", "modify\n");
        repository.Commit("Modify");
        GitClient client = Client(repository);

        GitFileHistoryPage first = await client.ReadFileHistoryAsync(new(repository.Path, "HEAD", "b.txt", 2));
        GitFileHistoryPage second = await client.ReadFileHistoryAsync(new(repository.Path, first.ResolvedOid, "b.txt", 2, 2));

        Assert.Equal([("Modify", "b.txt"), ("Rename", "b.txt")], first.Versions.Select(version => (version.Commit.Subject, version.Path)));
        Assert.True(first.HasMore);
        Assert.Equal([("Change", "a.txt"), ("Base", "a.txt")], second.Versions.Select(version => (version.Commit.Subject, version.Path)));
        Assert.False(second.HasMore);
    }

    private static GitClient Client(GitWorkflowRepository repository) => new(new RecordingLauncher(), repository.Paths, repository.Locks);

    // A merge of two branches that both changed a.txt, left in progress with a.txt unmerged.
    private static void Conflict(GitWorkflowRepository repository)
    {
        repository.Run("switch", "-q", "-c", "other");
        repository.Write("a.txt", "other\n");
        repository.Commit("Other");
        repository.Run("switch", "-q", "main");
        repository.Write("a.txt", "main\n");
        repository.Commit("Main");
        Assert.NotEqual(0, repository.TryRun("merge", "other"));
    }

    // Another clone pushes a new commit to origin's main, which this repository has not fetched.
    private static string PushFromOtherClone(GitWorkflowRepository repository)
    {
        string other = Path.Combine(repository.Root, "other");
        Git.Run(repository.Root, "clone", "-q", repository.Remote, other);
        File.WriteAllText(Path.Combine(other, "upstream.txt"), "upstream\n");
        Git.Run(other, "add", "upstream.txt");
        Git.Run(other, "commit", "-q", "-m", "Upstream");
        Git.Run(other, "push", "-q", "origin", "main");
        return Git.Run(other, "rev-parse", "HEAD").TrimEnd('\n');
    }
}
