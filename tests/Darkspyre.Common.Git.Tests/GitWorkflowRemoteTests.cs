using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// The library's Git transfers against a real local bare remote through a recording launcher: lease pushes, a push after
/// an unknown outcome, remote reads, fetch results from ref comparison, and a failed fetch of a force-pushed branch.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GitWorkflowRemoteTests
{
    /// <summary>G6.</summary>
    [Fact]
    public async Task ALeasePushReplacesTheRemoteBranchOnlyWithTheRightExpectedCommit()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-lease-");
        GitConversation git = repository.First;
        string root = repository.Head();
        repository.Write("a.txt", "published\n");
        string published = repository.Commit("Published");
        repository.Run("push", "-q", "origin", "main");
        repository.Run("reset", "-q", "--hard", root);
        repository.Write("a.txt", "rewritten\n");
        string rewritten = repository.Commit("Rewritten");

        GitPushResult wrong = await git.Transfers.PushAsync(new(repository.Path, "origin", "main", "refs/heads/main", GitPushMode.Lease, root));
        string kept = repository.RemoteHead("refs/heads/main");
        GitPushResult right = await git.Transfers.PushAsync(new(repository.Path, "origin", "main", "refs/heads/main", GitPushMode.Lease, published));

        Assert.Equal(GitPushOutcome.Rejected, Assert.Single(wrong.Refs).Outcome);
        Assert.Equal(published, kept);
        Assert.Equal(GitPushOutcome.Accepted, Assert.Single(right.Refs).Outcome);
        Assert.Null(right.Diagnostic);
        Assert.Equal(rewritten, repository.RemoteHead("refs/heads/main"));
    }

    /// <summary>
    /// G7: a push cancelled after dispatch is Unknown and never retried, and the next push of the same ref, from another
    /// conversation, is sent.
    /// </summary>
    [Fact]
    public async Task APushAfterAnUnknownOutcomeIsSent()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-push-unknown-");
        GitConversation first = repository.First;
        GitConversation second = repository.Open();
        string slow = Path.Combine(repository.Root, "slow");
        // While the marker exists the remote's hook sleeps and then refuses, so the cancelled push never lands.
        GitScripts.Write(Path.Combine(repository.Remote, "hooks", "pre-receive"), $"cat >/dev/null\nif [ -f '{slow}' ]; then sleep 3; exit 1; fi\nexit 0");
        await File.WriteAllTextAsync(slow, "");
        repository.Write("a.txt", "pushed\n");
        string pushed = repository.Commit("Pushed");

        using CancellationTokenSource cancel = new();
        Task<GitPushResult> pushing = first.Transfers.PushAsync(new(repository.Path, "origin", "main", "refs/heads/main"), cancel.Token);
        await first.StartedAsync("push");
        await Task.Delay(500);
        await cancel.CancelAsync();
        GitPushResult unknown = await pushing;
        File.Delete(slow);
        GitPushResult sent = await second.Transfers.PushAsync(new(repository.Path, "origin", "main", "refs/heads/main"));

        Assert.Equal(GitPushOutcome.Unknown, Assert.Single(unknown.Refs).Outcome);
        Assert.Equal(1, first.Launcher.Starts("push"));
        Assert.Equal(1, second.Launcher.Starts("push"));
        Assert.Equal(GitPushOutcome.Accepted, Assert.Single(sent.Refs).Outcome);
        Assert.Equal(pushed, repository.RemoteHead("refs/heads/main"));
    }

    /// <summary>G7: a confirmed remote read reports each requested ref, and a read that times out reports none.</summary>
    [Fact]
    public async Task ARemoteReadThatTimesOutReportsTimedOut()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-remote-read-");
        GitConversation git = repository.First;
        string root = repository.Head();
        string uploadPack = GitScripts.Write(Path.Combine(repository.Root, "slow-upload-pack"), "sleep 2\nexec git upload-pack \"$@\"");
        repository.Run("config", "remote.origin.uploadpack", uploadPack);
        GitTransfers impatient = new(git.Commands, git.Reader, repository.Locks, repository.Paths) { RemoteReadTimeout = TimeSpan.FromMilliseconds(500) };

        GitRemoteRefsResult confirmed = await git.Transfers.ReadRemoteRefsAsync(new(repository.Path, "origin", ["refs/heads/main", "refs/heads/absent"]));
        GitRemoteRefsResult timedOut = await impatient.ReadRemoteRefsAsync(new(repository.Path, "origin", ["refs/heads/main"]));

        Assert.Equal(GitRemoteReadOutcome.Confirmed, confirmed.Outcome);
        Assert.Equal([new GitRemoteRef("refs/heads/main", root), new GitRemoteRef("refs/heads/absent", null)], confirmed.Refs);
        Assert.Equal(GitRemoteReadOutcome.TimedOut, timedOut.Outcome);
        Assert.Empty(timedOut.Refs);
    }

    /// <summary>
    /// M4: a fetch reports, in request order, each mapping destination whose commit changed, with the commit before (null
    /// for a new ref) and after; an unchanged destination and a source-only ref are not reported. A destination whose name
    /// only prefixes an existing ref fails the fetch and reports no ref.
    /// </summary>
    [Fact]
    public async Task AFetchReportsUpdatedDestinationsFromRefComparison()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-fetch-compare-");
        GitConversation git = repository.First;
        string root = repository.Head();
        string other = Path.Combine(repository.Root, "other");
        Git.Run(repository.Root, "clone", "-q", repository.Remote, other);
        File.WriteAllText(Path.Combine(other, "a.txt"), "published\n");
        Git.Run(other, "commit", "-q", "-am", "Published");
        string published = Git.Run(other, "rev-parse", "HEAD").TrimEnd('\n');
        Git.Run(other, "push", "-q", "origin", "main", "main:feature", "main:steady");
        repository.Run("update-ref", "refs/remotes/origin/steady", published);
        repository.Run("update-ref", "refs/remotes/nest/child", root);

        GitFetchResult fetched = await git.Transfers.FetchAsync(new(repository.Path, "origin",
            ["refs/heads/feature:refs/remotes/origin/feature", "refs/heads/main:refs/remotes/origin/main",
                "refs/heads/steady:refs/remotes/origin/steady", "refs/heads/main"]));
        GitFetchResult nested = await git.Transfers.FetchAsync(new(repository.Path, "origin", ["refs/heads/main:refs/remotes/nest"]));

        Assert.Equal([new GitRefUpdate("refs/remotes/origin/feature", null, published),
            new GitRefUpdate("refs/remotes/origin/main", root, published)], fetched.UpdatedRefs);
        Assert.Null(fetched.Diagnostic);
        Assert.Equal(published, repository.Head("refs/remotes/origin/main"));
        Assert.Empty(nested.UpdatedRefs);
        Assert.Equal("Fetch did not complete successfully; inspect repository state before another action. No retry or cleanup was attempted.",
            nested.Diagnostic);
        Assert.Equal(root, repository.Head("refs/remotes/nest/child"));
    }

    /// <summary>
    /// M5: a fetch of a force-pushed branch is never forced, so Git refuses it and, the fetch being atomic, changes no ref;
    /// the result reports the failure and no updated ref. A forced mapping is invalid input.
    /// </summary>
    [Fact]
    public async Task AFetchOfAForcePushedBranchChangesNoRefAndReportsFailure()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-fetch-forced-");
        GitConversation git = repository.First;
        string root = repository.Head();
        string other = Path.Combine(repository.Root, "other");
        Git.Run(repository.Root, "clone", "-q", repository.Remote, other);
        File.WriteAllText(Path.Combine(other, "a.txt"), "published\n");
        Git.Run(other, "commit", "-q", "-am", "Published");
        Git.Run(other, "push", "-q", "origin", "main");
        repository.Run("fetch", "-q", "origin");
        string tracking = repository.Head("refs/remotes/origin/main");
        Git.Run(other, "reset", "-q", "--hard", root);
        File.WriteAllText(Path.Combine(other, "a.txt"), "rewritten\n");
        Git.Run(other, "commit", "-q", "-am", "Rewritten");
        Git.Run(other, "push", "-q", "--force", "origin", "main");

        GitFetchResult fetched = await git.Transfers.FetchAsync(new(repository.Path, "origin", ["refs/heads/main:refs/remotes/origin/main"]));
        ArgumentException forced = await Assert.ThrowsAsync<ArgumentException>(() => git.Transfers.FetchAsync(new(repository.Path, "origin", ["+refs/heads/main:refs/remotes/origin/main"])));

        Assert.Empty(fetched.UpdatedRefs);
        Assert.Equal("Fetch did not complete successfully; inspect repository state before another action. No retry or cleanup was attempted.",
            fetched.Diagnostic);
        Assert.Equal(tracking, repository.Head("refs/remotes/origin/main"));
        Assert.Contains("force", forced.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clone at a full ref or at a commit ID that no branch tip names ends on that commit with HEAD detached and a clean
    /// worktree; a branch name keeps the branch checked out.
    /// </summary>
    [Fact]
    public async Task ACloneAtAFullRefOrACommitIdEndsDetachedOnThatCommit()
    {
        await using GitWorkflowRepository repository = GitWorkflowRepository.Create("git-clone-revision-");
        GitConversation git = repository.First;
        string root = repository.Head();
        repository.Write("a.txt", "second\n");
        string second = repository.Commit("Second");
        repository.Run("push", "-q", "origin", "main");
        repository.Run("switch", "-q", "-c", "feature", root);
        repository.Write("b.txt", "feature\n");
        string feature = repository.Commit("Feature");
        repository.Run("push", "-q", "origin", "feature");

        GitRepositorySnapshot atCommit = await git.Transfers.CloneAsync(new(repository.Remote, Path.Combine(repository.Root, "at-commit"), root));
        GitRepositorySnapshot atRef = await git.Transfers.CloneAsync(new(repository.Remote, Path.Combine(repository.Root, "at-ref"), "refs/heads/feature"));
        GitRepositorySnapshot atBranch = await git.Transfers.CloneAsync(new(repository.Remote, Path.Combine(repository.Root, "at-branch"), "main"));

        Assert.Equal((root, (string?)null), (atCommit.HeadCommit, atCommit.BranchRef));
        Assert.Equal((feature, (string?)null), (atRef.HeadCommit, atRef.BranchRef));
        Assert.Equal((second, (string?)"refs/heads/main"), (atBranch.HeadCommit, atBranch.BranchRef));
        Assert.All((GitRepositorySnapshot[])[atCommit, atRef, atBranch], clone => Assert.Empty(clone.Status));
        Assert.Equal("feature\n", await File.ReadAllTextAsync(Path.Combine(repository.Root, "at-ref", "b.txt")));
    }
}
