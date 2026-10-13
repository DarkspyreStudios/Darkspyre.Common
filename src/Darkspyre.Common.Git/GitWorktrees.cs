using static Darkspyre.Common.Git.GitOperationChecks;
using static Darkspyre.Common.Git.GitPathChecks;

namespace Darkspyre.Common.Git;

/// <summary>Explicit worktree effects; never a worker strategy or ownership/cleanup scheduler.</summary>
internal sealed partial class GitWorktrees(GitCommands git, GitRepositoryReader reader, GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    internal async Task<GitWorktreeSnapshot> CreateAsync(GitWorktreeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorktreePath);
        if (!Path.IsPathFullyQualified(request.WorktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(request));
        PreparedGitWorktree prepared = await this.PrepareAsync(request.RepositoryPath, request.SourceRef,
            request.BranchName, cancellationToken).ConfigureAwait(false);
        return await this.CreatePreparedAsync(prepared, request.WorktreePath, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<GitWorktreeSnapshot> CreatePreparedAsync(PreparedGitWorktree prepared,
        string worktreePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Path.IsPathFullyQualified(worktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(worktreePath));
        if (string.IsNullOrWhiteSpace(prepared.BaseCommit) || prepared.BaseCommit.Length is not (40 or 64)
            || prepared.BaseCommit.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Prepared worktree creation requires a resolved commit, never a moving ref.", nameof(prepared));
        GitRepositoryReader.GitLayout repository = await this.reader.LocateAsync(prepared.RepositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        if (repository.CommonDirectory != prepared.CommonDirectory) throw new IOException("The prepared repository identity changed.");
        string destination = this.paths.Resolve(worktreePath);
        await this.paths.RequireWriteAsync([repository.CommonDirectory, destination], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        if (NamedPathExists(worktreePath))
            throw new IOException("The requested worktree destination already exists.");
        ValidateBranchName(prepared.BranchName);
        PreparedGitWorktree current = await this.PrepareUnderLockAsync(repository, prepared.BaseCommit,
            prepared.BranchName, cancellationToken).ConfigureAwait(false);
        string branchRef = "refs/heads/" + current.BranchName;
        Check(await this.git.RunAsync(current.RepositoryPath,
            ["worktree", "add", "--no-track", "-b", current.BranchName, "--", destination, current.BaseCommit], cancellationToken).ConfigureAwait(false), "create worktree");
        GitRepositorySnapshot created = await this.reader.InspectAsync(destination, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Git did not leave a readable worktree; no automatic cleanup was attempted.");
        if (created.CommonDirectory != current.CommonDirectory || created.BranchRef != branchRef)
            throw new IOException("The created worktree identity changed; no automatic cleanup was attempted.");
        return new(created.CommonDirectory, created.WorktreePath, branchRef, current.BaseCommit);
    }

    /// <summary>Reattaches a safely removed worktree to its retained branch without creating or moving any ref.</summary>
    internal async Task<GitWorktreeSnapshot> ReattachPreparedAsync(PreparedGitWorktree prepared,
        string worktreePath, string expectedHead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Path.IsPathFullyQualified(worktreePath)) throw new ArgumentException("A worktree needs an exact absolute path.", nameof(worktreePath));
        if (string.IsNullOrWhiteSpace(expectedHead) || expectedHead.Length is not (40 or 64)
            || expectedHead.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Reattachment requires the recorded retained commit.", nameof(expectedHead));
        ValidateBranchName(prepared.BranchName);
        GitRepositoryReader.GitLayout repository = await this.reader.LocateAsync(prepared.RepositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        if (repository.CommonDirectory != prepared.CommonDirectory) throw new IOException("The recorded repository identity changed.");
        string destination = this.paths.Resolve(worktreePath);
        await this.paths.RequireWriteAsync([repository.CommonDirectory, destination], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        if (NamedPathExists(worktreePath)) throw new IOException("The archived worktree destination is already occupied.");
        string branchRef = "refs/heads/" + prepared.BranchName;
        Check(await this.git.RunAsync(prepared.RepositoryPath, ["check-ref-format", branchRef], cancellationToken).ConfigureAwait(false),
            "validate the retained branch");
        string head = await this.reader.ResolveCommitAsync(prepared.RepositoryPath, branchRef, cancellationToken).ConfigureAwait(false);
        if (head != expectedHead) throw new IOException("The retained worker branch changed since archival.");
        // Git refuses an already-checked-out branch. No force, branch creation, ref reset or fallback is permitted.
        Check(await this.git.RunAsync(prepared.RepositoryPath, ["worktree", "add", "--no-guess-remote", "--", destination, prepared.BranchName],
            cancellationToken).ConfigureAwait(false), "reattach the retained worker branch");
        GitRepositorySnapshot created = await this.reader.InspectAsync(destination, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Git did not leave a readable retained worktree.");
        if (created.CommonDirectory != prepared.CommonDirectory || created.BranchRef != branchRef || created.HeadCommit != expectedHead)
            throw new IOException("The reattached worktree identity changed; no automatic cleanup was attempted.");
        return new(created.CommonDirectory, created.WorktreePath, branchRef, expectedHead);
    }

    /// <summary>
    /// Deletes the branch <paramref name="created"/> made, only while it still names the commit it was created at. A
    /// creation that is rolled back uses it after the worktree is removed.
    /// </summary>
    internal async Task DeleteCreatedBranchAsync(GitWorktreeSnapshot created, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(created);
        using IDisposable operation = await this.locks.EnterAsync(created.CommonDirectory, cancellationToken).ConfigureAwait(false);
        Check(await this.git.RunAsync(created.CommonDirectory, ["update-ref", "-d", created.BranchRef, created.BaseCommit],
            cancellationToken).ConfigureAwait(false), "delete the created branch");
    }

    internal Task<GitRemovalResult> RemoveAsync(string worktreePath,
        CancellationToken cancellationToken = default) => this.RemoveCoreAsync(worktreePath, null, false, cancellationToken);

    /// <summary>
    /// Removes a worktree like <see cref="RemoveAsync"/>, except that ignored files, such as build output, do not block
    /// removal and are deleted with the worktree.
    /// </summary>
    internal Task<GitRemovalResult> RemoveDiscardingIgnoredAsync(string worktreePath,
        CancellationToken cancellationToken = default) => this.RemoveCoreAsync(worktreePath, null, true, cancellationToken);

    /// <summary>Core-owned cleanup additionally requires the recorded repository and retained branch.</summary>
    internal Task<GitRemovalResult> RemoveOwnedAsync(string worktreePath,
        PreparedGitWorktree expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return this.RemoveCoreAsync(worktreePath, expected, false, cancellationToken);
    }

    private async Task<GitRemovalResult> RemoveCoreAsync(string worktreePath,
        PreparedGitWorktree? expected, bool ignoredFilesExpendable, CancellationToken cancellationToken)
    {
        bool Removable(GitPreservationCheck check) =>
            ignoredFilesExpendable ? GitPreservationCheck.LosesOnlyIgnoredFiles(check) : check.CanRemoveWithoutLoss;
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Path.IsPathFullyQualified(worktreePath)) throw new ArgumentException("Removal requires the exact absolute worktree path.", nameof(worktreePath));
        GitRepositoryReader.GitLayout? repository;
        try { repository = await this.reader.LocateAsync(worktreePath, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or NotSupportedException) { repository = null; }
        if (repository is null)
        {
            // Not a readable working tree: its preservation check says why, and nothing is removed.
            GitPreservationCheck unreadable = await this.reader.CheckPreservationAsync(worktreePath, cancellationToken).ConfigureAwait(false);
            return new(GitRemovalStatus.Retained, unreadable,
                await this.ExistsAsync(worktreePath, cancellationToken).ConfigureAwait(false) ? worktreePath : null, unreadable.BranchRef);
        }
        using IDisposable operation = await this.locks.EnterAsync(repository.CommonDirectory, cancellationToken).ConfigureAwait(false);
        GitPreservationCheck checkedNow = await this.reader.CheckPreservationAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        if (!Removable(checkedNow)) return new(GitRemovalStatus.Retained, checkedNow,
            await this.ExistsAsync(worktreePath, cancellationToken).ConfigureAwait(false) ? worktreePath : null, checkedNow.BranchRef);
        GitRepositorySnapshot current = await this.reader.InspectAsync(worktreePath, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The worktree changed while waiting for its operation gate.");
        if (expected is not null && (current.CommonDirectory != expected.CommonDirectory
            || current.BranchRef != "refs/heads/" + expected.BranchName || current.WorktreePath != worktreePath))
            return new(GitRemovalStatus.Retained, checkedNow, worktreePath, current.BranchRef,
                "The worktree no longer matches Core's recorded repository, path or retained branch.");
        if (current.CommonDirectory != repository.CommonDirectory || current.BranchRef != checkedNow.BranchRef
            || current.HeadCommit != checkedNow.HeadCommit
            || current.Status.Any(entry => !ignoredFilesExpendable || !entry.IsIgnored))
            return new(GitRemovalStatus.Retained, checkedNow, worktreePath, current.BranchRef, "Worktree identity or state changed during removal preparation.");
        try { await this.paths.RequireWriteAsync([current.CommonDirectory, current.WorktreePath], cancellationToken).ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            return new(GitRemovalStatus.Retained, checkedNow, worktreePath, current.BranchRef, "Current scope policy does not permit worktree removal.");
        }
        // Execute from persistent shared metadata, not the directory being removed. Never force,
        // recursively delete directly, remove a ref, push or merge as a side effect of cleanup.
        GitRunResult removed = await this.git.RunAsync(current.CommonDirectory,
            ["worktree", "remove", "--", current.WorktreePath], cancellationToken).ConfigureAwait(false);
        bool remains = await this.ExistsAsync(current.WorktreePath, CancellationToken.None).ConfigureAwait(false);
        return removed.Exit.ExitCode == 0 && removed.Exit.Diagnostic is null && !remains
            ? new(GitRemovalStatus.Removed, checkedNow, null, current.BranchRef)
            : new(GitRemovalStatus.Failed, checkedNow, remains ? current.WorktreePath : null, current.BranchRef,
                "Git did not confirm successful worktree removal; no retry or force was attempted.");
    }

    // Whether something is at the path now. A path the policy refuses throws UnauthorizedAccessException.
    private async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        GitPathAccess access = await this.paths.GetReadAccessAsync(path, cancellationToken).ConfigureAwait(false);
        if (access == GitPathAccess.Refused) throw new UnauthorizedAccessException("The path policy refuses reading the path.");
        return access != GitPathAccess.Absent;
    }
}
