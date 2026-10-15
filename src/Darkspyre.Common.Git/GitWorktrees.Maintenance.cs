using static Darkspyre.Common.Git.GitOperationChecks;
using static Darkspyre.Common.Git.GitPathChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitWorktrees
{
    /// <summary>
    /// Locks the linked worktree at <paramref name="worktreePath"/> with <paramref name="reason"/>. A worktree that is
    /// already locked keeps its lock and reason.
    /// </summary>
    internal async Task LockAsync(string worktreePath, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterWorktreeAsync(worktreePath, cancellationToken)
            .ConfigureAwait(false);
        using (operation)
        {
            if ((await this.ListedAsync(layout, cancellationToken).ConfigureAwait(false)).Locked) return;
            Check(await this.git.RunAsync(layout.WorktreePath, ["worktree", "lock", "--reason", reason, "--", layout.WorktreePath],
                cancellationToken).ConfigureAwait(false), "lock worktree");
        }
    }

    /// <summary>Unlocks the linked worktree at <paramref name="worktreePath"/>. A worktree that is not locked is left as it is.</summary>
    internal async Task UnlockAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterWorktreeAsync(worktreePath, cancellationToken)
            .ConfigureAwait(false);
        using (operation)
        {
            if (!(await this.ListedAsync(layout, cancellationToken).ConfigureAwait(false)).Locked) return;
            Check(await this.git.RunAsync(layout.WorktreePath, ["worktree", "unlock", "--", layout.WorktreePath], cancellationToken)
                .ConfigureAwait(false), "unlock worktree");
        }
    }

    /// <summary>
    /// Unlocks the repository's record of the linked worktree at <paramref name="worktreePath"/>, whose folder may be gone.
    /// A record that is not locked, or no record, is left as it is. Prune keeps a locked record, so a caller that re-creates
    /// a deleted locked worktree unlocks its record first.
    /// </summary>
    internal async Task UnlockRecordAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Path.IsPathFullyQualified(worktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(worktreePath));
        GitRepositoryReader.GitLayout repository = await this.reader.LocateAsync(repositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        await this.paths.RequireWriteAsync([repository.CommonDirectory], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        string wanted = this.paths.Resolve(worktreePath);
        byte[] output = await this.reader.ReadAsync(repository.WorktreePath, ["worktree", "list", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        // Git prints '/' on every system; the path policy resolves both sides to one form.
        if (GitWorktreeList.Parse(output).FirstOrDefault(entry => this.paths.Resolve(entry.Path) == wanted) is not { Locked: true } record) return;
        Check(await this.git.RunAsync(repository.WorktreePath, ["worktree", "unlock", "--", record.Path], cancellationToken)
            .ConfigureAwait(false), "unlock worktree record");
    }

    /// <summary>Runs <c>git worktree prune</c>, which drops the records of worktrees whose folders are gone.</summary>
    internal async Task PruneAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        GitRepositoryReader.GitLayout repository = await this.reader.LocateAsync(repositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        await this.paths.RequireWriteAsync([repository.CommonDirectory], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        Check(await this.git.RunAsync(repository.WorktreePath, ["worktree", "prune"], cancellationToken).ConfigureAwait(false),
            "prune worktrees");
    }

    /// <summary>
    /// Adds a linked worktree on the request's branch: the local branch when it exists, otherwise a new local branch at
    /// <c>origin/</c> the branch when that exists, otherwise a new local branch at the base. A new branch tracks nothing.
    /// </summary>
    internal async Task<GitWorktreeAttachResult> AttachAsync(GitWorktreeAttachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseRef);
        if (!Path.IsPathFullyQualified(request.WorktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(request));
        ValidateBranchName(request.BranchName);
        GitRepositoryReader.GitLayout repository = await this.reader.LocateAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        string destination = this.paths.Resolve(request.WorktreePath);
        await this.paths.RequireWriteAsync([repository.CommonDirectory, destination], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        if (NamedPathExists(request.WorktreePath)) throw new IOException("The requested worktree destination already exists.");
        string branchRef = "refs/heads/" + request.BranchName;
        Check(await this.git.RunAsync(repository.WorktreePath, ["check-ref-format", branchRef], cancellationToken).ConfigureAwait(false),
            "validate branch name");
        GitWorktreeAttachSource source;
        if (await this.RefExistsAsync(repository, branchRef, cancellationToken).ConfigureAwait(false))
        {
            source = GitWorktreeAttachSource.LocalBranch;
            // Git refuses a branch another worktree has checked out. Nothing is forced.
            Check(await this.git.RunAsync(repository.WorktreePath, ["worktree", "add", "--no-guess-remote", "--", destination,
                request.BranchName], cancellationToken).ConfigureAwait(false), "attach worktree");
        }
        else
        {
            string remoteRef = "refs/remotes/origin/" + request.BranchName;
            bool remote = await this.RefExistsAsync(repository, remoteRef, cancellationToken).ConfigureAwait(false);
            source = remote ? GitWorktreeAttachSource.RemoteBranch : GitWorktreeAttachSource.NewBranch;
            string commit = await this.reader.ResolveCommitAsync(repository.WorktreePath, remote ? remoteRef : request.BaseRef,
                cancellationToken).ConfigureAwait(false);
            Check(await this.git.RunAsync(repository.WorktreePath, ["worktree", "add", "--no-track", "-b", request.BranchName, "--",
                destination, commit], cancellationToken).ConfigureAwait(false), "create worktree");
        }
        GitRepositorySnapshot created = await this.reader.InspectAsync(destination, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Git did not leave a readable worktree; no automatic cleanup was attempted.");
        if (created.CommonDirectory != repository.CommonDirectory || created.BranchRef != branchRef || created.HeadCommit is not { } head)
            throw new IOException("The attached worktree identity changed; no automatic cleanup was attempted.");
        return new(new(created.CommonDirectory, created.WorktreePath, branchRef, head), source);
    }

    // The worktree's layout and the repository's operation gate, entered after the write check of its common directory.
    private async Task<(GitRepositoryReader.GitLayout Layout, IDisposable Operation)> EnterWorktreeAsync(string worktreePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Path.IsPathFullyQualified(worktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(worktreePath));
        GitRepositoryReader.GitLayout layout = await this.reader.LocateAsync(worktreePath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The worktree is unavailable.");
        await this.paths.RequireWriteAsync([layout.CommonDirectory], cancellationToken).ConfigureAwait(false);
        return (layout, await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false));
    }

    // The worktree's own record in git worktree list --porcelain.
    private async Task<GitWorktreeEntry> ListedAsync(GitRepositoryReader.GitLayout layout, CancellationToken cancellationToken)
    {
        byte[] output = await this.reader.ReadAsync(layout.WorktreePath, ["worktree", "list", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        // Git prints '/' on every system; the path policy resolves both sides to one form.
        return GitWorktreeList.Parse(output).FirstOrDefault(entry => this.paths.Resolve(entry.Path) == layout.WorktreePath)
            ?? throw new IOException("Git does not list the worktree.");
    }

    // Whether the full ref exists: show-ref exits 0 when it does and 1 when it does not.
    private async Task<bool> RefExistsAsync(GitRepositoryReader.GitLayout repository, string fullRef, CancellationToken cancellationToken)
    {
        GitRunResult existing = await this.git.RunAsync(repository.WorktreePath, ["show-ref", "--verify", "--quiet", fullRef],
            cancellationToken).ConfigureAwait(false);
        if (existing.Exit.ExitCode is 0 or 1) return existing.Exit.ExitCode == 0;
        Check(existing, "check a ref");
        return false;
    }
}
