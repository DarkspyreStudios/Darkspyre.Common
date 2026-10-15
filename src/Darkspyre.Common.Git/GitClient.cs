namespace Darkspyre.Common.Git;

/// <summary>
/// The Git operations of one caller: every child starts through the process launcher, every caller path is checked
/// through the path policy, and changes are serialized through the repository locks.
/// </summary>
/// <gotcha>The client checks no Git version. Read <see cref="GitVersion"/> once before the first call; an older Git fails per command.</gotcha>
public sealed class GitClient
{
    private readonly GitRepositoryReader reader;
    private readonly GitTransfers transfers;
    private readonly GitWorktrees worktrees;
    private readonly GitCommits commits;
    private readonly GitWorkspaceTransitions transitions;
    private readonly GitSequences sequences;
    private readonly GitLocalChanges local;

    /// <summary>Creates the client over one process launcher, one path policy and the process's shared repository locks.</summary>
    /// <param name="processes">Starts every Git child.</param>
    /// <param name="paths">Resolves and checks every caller path.</param>
    /// <param name="locks">Serializes changes per repository; share one instance among the clients of one process.</param>
    /// <param name="readPolicy">Transport policy for reads. Explicit mutations retain their existing behavior.</param>
    public GitClient(IGitProcessLauncher processes, IGitPathPolicy paths, GitRepositoryLocks locks,
        GitReadPolicy readPolicy = GitReadPolicy.Configured)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(locks);
        if (!Enum.IsDefined(readPolicy)) throw new ArgumentOutOfRangeException(nameof(readPolicy));
        GitCommands git = new(processes, readPolicy);
        this.reader = new(git, paths);
        this.transfers = new(git, this.reader, locks, paths);
        this.worktrees = new(git, this.reader, locks, paths);
        this.commits = new(git, this.reader, locks, paths);
        this.transitions = new(git, this.reader, locks, paths);
        this.sequences = new(git, this.reader, locks, paths);
        this.local = new(git, this.reader, locks, paths);
    }

    /// <summary>Reads the working tree containing <paramref name="directory"/>, or returns null when it is not inside a Git repository.</summary>
    public Task<GitRepositorySnapshot?> InspectAsync(string directory, CancellationToken cancellationToken = default) =>
        this.reader.InspectAsync(directory, cancellationToken);

    /// <summary>
    /// Lists registered worktrees, including detached and unavailable locations, leaving out paths the policy refuses.
    /// It never prunes entries, changes a checkout or contacts a remote. A failed inventory throws.
    /// </summary>
    public Task<IReadOnlyList<GitWorktreeEntry>> ReadWorktreesAsync(string directory, CancellationToken cancellationToken = default) =>
        this.reader.ReadWorktreesAsync(directory, cancellationToken);

    /// <summary>
    /// Lists local branch refs and their observed commit IDs without reading content or contacting a remote.
    /// A failed inventory throws; a successful empty list means no local branches exist.
    /// </summary>
    public Task<IReadOnlyList<GitBranchEntry>> ReadLocalBranchesAsync(string directory, CancellationToken cancellationToken = default) =>
        this.reader.ReadLocalBranchesAsync(directory, cancellationToken);

    /// <summary>Clones a remote into a new absolute destination and returns the resulting repository. A failed clone is not cleaned up.</summary>
    public Task<GitRepositorySnapshot> CloneAsync(GitCloneRequest request, CancellationToken cancellationToken = default) =>
        this.transfers.CloneAsync(request, cancellationToken);

    /// <summary>Fetches the requested refs from a configured remote and reports the refs it updated.</summary>
    public Task<GitFetchResult> FetchAsync(GitFetchRequest request, CancellationToken cancellationToken = default) =>
        this.transfers.FetchAsync(request, cancellationToken);

    /// <summary>
    /// Reads one file's committed bytes at a commit or ref into memory, after checking the path against the path
    /// policy. It never writes the worktree.
    /// </summary>
    /// <gotcha>A reference that does not resolve throws <see cref="GitOperationException"/> with <see cref="GitFailure.RevisionNotFound"/>;
    /// a directory or submodule throws <see cref="GitReadException"/> with <see cref="GitReadFailure.NotBlob"/>; a file longer than
    /// <see cref="GitFileAtCommitRequest.MaximumBytes"/> throws it with <see cref="GitReadFailure.LimitExceeded"/> before its bytes are read; an object missing from the
    /// local object store throws it with <see cref="GitReadFailure.ObjectMissing"/> and nothing is fetched.</gotcha>
    public Task<GitFileContent> ReadFileAtCommitAsync(GitFileAtCommitRequest request, CancellationToken cancellationToken = default) =>
        this.reader.ReadFileAtCommitAsync(request, cancellationToken);

    /// <summary>
    /// Lists the files, links and submodules of one commit under a folder, sorted by path, leaving out every entry the
    /// path policy refuses.
    /// </summary>
    public Task<IReadOnlyList<GitTreeEntry>> ReadTreeAsync(GitTreeRequest request, CancellationToken cancellationToken = default) =>
        this.reader.ReadTreeAsync(request, cancellationToken);

    /// <summary>
    /// Lists the repository's configured remotes with their fetch and push destinations, after Git's URL rewriting, as
    /// sanitized structured endpoints. It never returns a raw URL, a user name or a credential, and never contacts a remote.
    /// </summary>
    public Task<IReadOnlyList<GitRemoteInfo>> ReadRemotesAsync(string directory, CancellationToken cancellationToken = default) =>
        this.reader.ReadRemoteEndpointsAsync(directory, cancellationToken);

    /// <summary>
    /// Resolves <paramref name="reference"/> once to a local commit. A reference that resolves to no commit is
    /// <see cref="GitCommitResolutionStatus.UnresolvedReference"/>, not an exception.
    /// </summary>
    public Task<GitCommitResolution> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default) =>
        this.reader.ResolveExactCommitAsync(directory, reference, cancellationToken);

    /// <summary>Reads the changed files and raw patch for a comparison.</summary>
    public Task<GitDiffResult> ReadDiffAsync(GitDiffRequest request, CancellationToken cancellationToken = default) =>
        this.reader.ReadDiffAsync(request, cancellationToken);

    /// <summary>
    /// Pushes one commit to one remote branch. After dispatch, cancellation or a lost acknowledgement returns Unknown, never
    /// an inferred rejection or an automatic retry.
    /// </summary>
    public Task<GitPushResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken = default) =>
        this.transfers.PushAsync(request, cancellationToken);

    /// <summary>Creates a linked worktree on a new branch at the resolved source commit. The branch must not already exist.</summary>
    public Task<GitWorktreeSnapshot> CreateWorktreeAsync(GitWorktreeRequest request, CancellationToken cancellationToken = default) =>
        this.worktrees.CreateAsync(request, cancellationToken);

    /// <summary>Stages and commits only the requested paths on the checked-out branch. Repository hooks run as Git runs them.</summary>
    public Task<GitCommitResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken = default) =>
        this.commits.CommitAsync(request, cancellationToken);

    /// <summary>Reports what removing or resetting the worktree at <paramref name="worktreePath"/> would lose.</summary>
    public Task<GitPreservationCheck> CheckPreservationAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        this.reader.CheckPreservationAsync(worktreePath, cancellationToken);

    /// <summary>Removes a linked worktree only when nothing would be lost; otherwise retains it. It never forces removal or deletes the branch.</summary>
    public Task<GitRemovalResult> RemoveWorktreeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        this.worktrees.RemoveAsync(worktreePath, cancellationToken);

    /// <summary>Switches a preserved linked worktree at the expected HEAD to another branch and removes its untracked files.</summary>
    public Task<GitResetResult> ResetWorktreeAsync(GitResetRequest request, CancellationToken cancellationToken = default) =>
        this.transitions.ResetAsync(request, cancellationToken);

    /// <summary>Creates a branch at the resolved start point without switching to it, while HEAD is the expected commit.</summary>
    /// <gotcha>
    /// An existing branch of that name throws <see cref="GitOperationException"/> with <see cref="GitFailure.BranchExists"/>;
    /// a start point that does not resolve throws it with <see cref="GitFailure.RevisionNotFound"/>.
    /// </gotcha>
    public Task<GitBranchResult> CreateBranchAsync(GitBranchCreateRequest request, CancellationToken cancellationToken = default) =>
        this.local.CreateBranchAsync(request, cancellationToken);

    /// <summary>Deletes a branch, merged or not, while it names the expected commit.</summary>
    /// <gotcha>
    /// A branch checked out in any worktree throws <see cref="GitOperationException"/> with
    /// <see cref="GitFailure.BranchCheckedOut"/>; a branch at another commit throws it with <see cref="GitFailure.StaleState"/>,
    /// and a missing branch with <see cref="GitFailure.RevisionNotFound"/>. Nothing is deleted in each case.
    /// </gotcha>
    public Task<GitBranchResult> DeleteBranchAsync(GitBranchDeleteRequest request, CancellationToken cancellationToken = default) =>
        this.local.DeleteBranchAsync(request, cancellationToken);

    /// <summary>
    /// Starts, continues or aborts a merge into the worktree's HEAD. A start always creates a merge commit with the given
    /// message and never opens an editor. A conflict leaves the merge in progress.
    /// </summary>
    /// <gotcha>
    /// A start needs a worktree with no change other than untracked files and no operation in progress
    /// (<see cref="GitFailure.NotClean"/>). Continue and abort need a merge in progress (<see cref="GitFailure.StaleState"/>
    /// otherwise); continue with unmerged paths throws <see cref="GitFailure.NotClean"/>.
    /// <see cref="GitSequenceResult.NewCommits"/> names the merge commit, or is empty when the source was already merged.
    /// </gotcha>
    public Task<GitSequenceResult> MergeAsync(GitMergeRequest request, CancellationToken cancellationToken = default) =>
        this.sequences.MergeAsync(request, cancellationToken);

    /// <summary>Stashes the worktree's changes with a message.</summary>
    /// <gotcha>A worktree with nothing to stash returns a result whose stash ref and commit are null.</gotcha>
    public Task<GitStashSaveResult> SaveStashAsync(GitStashSaveRequest request, CancellationToken cancellationToken = default) =>
        this.local.SaveStashAsync(request, cancellationToken);

    /// <summary>Lists the repository's stashes, newest first.</summary>
    public Task<IReadOnlyList<GitStashEntry>> ListStashesAsync(string repository, CancellationToken cancellationToken = default) =>
        this.local.ListStashesAsync(repository, cancellationToken);

    /// <summary>Applies the stash with the given commit to the worktree. The stash is kept, also after a conflict.</summary>
    /// <gotcha>
    /// No stash with that commit throws <see cref="GitOperationException"/> with <see cref="GitFailure.StashNotFound"/>. A
    /// failure without conflicted paths throws it with <see cref="GitFailure.CommandFailed"/>.
    /// </gotcha>
    public Task<GitStashApplyResult> ApplyStashAsync(GitStashApplyRequest request, CancellationToken cancellationToken = default) =>
        this.local.ApplyStashAsync(request, cancellationToken);

    /// <summary>Drops the stash with the given commit.</summary>
    /// <gotcha>No stash with that commit throws <see cref="GitOperationException"/> with <see cref="GitFailure.StashNotFound"/>.</gotcha>
    public Task<GitStashDropResult> DropStashAsync(GitStashDropRequest request, CancellationToken cancellationToken = default) =>
        this.local.DropStashAsync(request, cancellationToken);

    /// <summary>Writes the named paths from a revision into the worktree only. The index is not changed.</summary>
    /// <gotcha>A path absent at the revision is reported in <see cref="GitRestoreResult.MissingPaths"/>; the other paths are still restored.</gotcha>
    public Task<GitRestoreResult> RestorePathsAsync(GitRestoreRequest request, CancellationToken cancellationToken = default) =>
        this.local.RestorePathsAsync(request, cancellationToken);

    /// <summary>
    /// Reads the working tree containing <paramref name="repository"/>: HEAD, the branch and its upstream from local refs,
    /// the changes and any operation in progress. It returns null when the directory is not inside a working tree, and
    /// never fetches.
    /// </summary>
    /// <gotcha>
    /// A change or conflicted path the path policy refuses is left out without a count. The upstream counts
    /// compare local refs only, so they are as fresh as the last fetch.
    /// </gotcha>
    public Task<GitStatusSnapshot?> ReadStatusAsync(string repository, CancellationToken cancellationToken = default) =>
        this.reader.ReadStatusAsync(repository, cancellationToken);

    /// <summary>
    /// Reads one page of commit summaries reachable from a revision, newest first, optionally limited to commits that touch
    /// the named paths. The revision resolves once; every path filter must be readable in the path policy.
    /// </summary>
    /// <gotcha>
    /// The host keeps no cursor. Read the next page with <see cref="GitHistoryPage.ResolvedOid"/> as the revision and the
    /// number of commits already returned as <see cref="GitHistoryRequest.Skip"/>, so a moved branch changes nothing.
    /// </gotcha>
    public Task<GitHistoryPage> ReadHistoryAsync(GitHistoryRequest request, CancellationToken cancellationToken = default) =>
        this.reader.ReadHistoryAsync(request, cancellationToken);

    /// <summary>
    /// Resolves two revisions once and reports their merge bases, how many commits each has that the other lacks, and
    /// whether one contains the other.
    /// </summary>
    public Task<GitAncestryResult> ReadAncestryAsync(GitAncestryRequest request, CancellationToken cancellationToken = default) =>
        this.reader.ReadAncestryAsync(request, cancellationToken);

    /// <summary>
    /// Reads the named refs from the remote's single push destination with <c>git ls-remote</c>, waiting at most 30
    /// seconds.
    /// </summary>
    /// <gotcha>
    /// This contacts the remote. A failed or timed-out read reports no refs. The remote's URL never appears in the result.
    /// </gotcha>
    public Task<GitRemoteRefsResult> ReadRemoteRefsAsync(GitRemoteRefsRequest request, CancellationToken cancellationToken = default) =>
        this.transfers.ReadRemoteRefsAsync(request, cancellationToken);

    /// <summary>
    /// Stages exactly the named paths. Entries staged earlier stay staged. A deleted tracked path is staged as a deletion.
    /// </summary>
    public Task<GitStageResult> StageAsync(GitStageRequest request, CancellationToken cancellationToken = default) =>
        this.commits.StageAsync(request, cancellationToken);

    /// <summary>
    /// Starts, continues or aborts a rebase of the worktree's branch. Git never opens an editor: every message is kept as
    /// it is. A conflict leaves the rebase in progress.
    /// </summary>
    /// <gotcha>
    /// A start needs a worktree with no change other than untracked files and no operation in progress
    /// (<see cref="GitFailure.NotClean"/>). Continue and abort need a rebase in progress (<see cref="GitFailure.StaleState"/>
    /// otherwise); continue with unmerged paths throws <see cref="GitFailure.NotClean"/>.
    /// </gotcha>
    public Task<GitSequenceResult> RebaseAsync(GitRebaseRequest request, CancellationToken cancellationToken = default) =>
        this.sequences.RebaseAsync(request, cancellationToken);

    /// <summary>
    /// Starts, continues or aborts a cherry-pick of 1 to 64 commits in the given order. A commit whose changes are already
    /// in HEAD is dropped and named in <see cref="GitSequenceResult.SkippedCommits"/>. Git never opens an editor.
    /// </summary>
    /// <gotcha>
    /// A merge commit or a commit that starts empty is refused with <see cref="ArgumentException"/> before anything
    /// changes. Continue and abort need a cherry-pick in progress.
    /// </gotcha>
    public Task<GitSequenceResult> CherryPickAsync(GitCherryPickRequest request, CancellationToken cancellationToken = default) =>
        this.sequences.CherryPickAsync(request, cancellationToken);

    /// <summary>
    /// Resolves <paramref name="sourceRef"/> to a commit under the repository's operation gate and checks that the new
    /// branch does not exist yet. It reserves nothing and changes nothing.
    /// </summary>
    public Task<PreparedGitWorktree> PrepareWorktreeAsync(string repositoryPath, string sourceRef, string branchName, CancellationToken cancellationToken = default) =>
        this.worktrees.PrepareAsync(repositoryPath, sourceRef, branchName, cancellationToken);

    /// <summary>Creates a linked worktree on the prepared new branch at the prepared commit.</summary>
    public Task<GitWorktreeSnapshot> CreatePreparedWorktreeAsync(PreparedGitWorktree prepared, string worktreePath, CancellationToken cancellationToken = default) =>
        this.worktrees.CreatePreparedAsync(prepared, worktreePath, cancellationToken);

    /// <summary>
    /// Reattaches a safely removed worktree to its retained branch, which must still name <paramref name="expectedHead"/>.
    /// It never creates or moves a ref.
    /// </summary>
    public Task<GitWorktreeSnapshot> ReattachPreparedWorktreeAsync(PreparedGitWorktree prepared, string worktreePath, string expectedHead, CancellationToken cancellationToken = default) =>
        this.worktrees.ReattachPreparedAsync(prepared, worktreePath, expectedHead, cancellationToken);

    /// <summary>
    /// Removes a worktree like <see cref="RemoveWorktreeAsync"/>, except that ignored files, such as build
    /// output, do not block removal and are deleted with the worktree.
    /// </summary>
    public Task<GitRemovalResult> RemoveWorktreeDiscardingIgnoredAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        this.worktrees.RemoveDiscardingIgnoredAsync(worktreePath, cancellationToken);

    /// <summary>Removes a worktree a core module created, only while it still holds the recorded repository and branch.</summary>
    public Task<GitRemovalResult> RemoveOwnedWorktreeAsync(string worktreePath, PreparedGitWorktree expected, CancellationToken cancellationToken = default) =>
        this.worktrees.RemoveOwnedAsync(worktreePath, expected, cancellationToken);

    /// <summary>
    /// Adds a linked worktree on the request's branch: the local branch when it exists, otherwise a new local branch at
    /// <c>origin/</c> the branch, otherwise a new local branch at the request's base.
    /// </summary>
    public Task<GitWorktreeAttachResult> AttachWorktreeAsync(GitWorktreeAttachRequest request, CancellationToken cancellationToken = default) =>
        this.worktrees.AttachAsync(request, cancellationToken);

    /// <summary>Locks a linked worktree with <paramref name="reason"/>; an already locked worktree keeps its lock.</summary>
    /// <gotcha>Git refuses to remove a locked worktree. Unlock it first with <see cref="UnlockWorktreeAsync"/>.</gotcha>
    public Task LockWorktreeAsync(string worktreePath, string reason, CancellationToken cancellationToken = default) =>
        this.worktrees.LockAsync(worktreePath, reason, cancellationToken);

    /// <summary>Unlocks a linked worktree; a worktree that is not locked is left as it is.</summary>
    public Task UnlockWorktreeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        this.worktrees.UnlockAsync(worktreePath, cancellationToken);

    /// <summary>
    /// Unlocks the repository's record of a linked worktree whose folder may be gone; a record that is not locked is left
    /// as it is.
    /// </summary>
    /// <gotcha><see cref="PruneWorktreesAsync"/> keeps a locked record, and its branch stays checked out there.</gotcha>
    public Task UnlockWorktreeRecordAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken = default) =>
        this.worktrees.UnlockRecordAsync(repositoryPath, worktreePath, cancellationToken);

    /// <summary>Drops the repository's records of worktrees whose folders are gone (<c>git worktree prune</c>).</summary>
    public Task PruneWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        this.worktrees.PruneAsync(repositoryPath, cancellationToken);

    /// <summary>Deletes the branch <paramref name="created"/> made, only while it still names the commit it was created at.</summary>
    public Task DeleteCreatedBranchAsync(GitWorktreeSnapshot created, CancellationToken cancellationToken = default) =>
        this.worktrees.DeleteCreatedBranchAsync(created, cancellationToken);

    /// <summary>The commits reachable from <paramref name="commit"/> that no remote-tracking branch reaches.</summary>
    public Task<IReadOnlyList<string>> ReadUnpublishedCommitsAsync(string repository, string commit, CancellationToken cancellationToken = default) =>
        this.reader.ReadUnpublishedCommitsAsync(repository, commit, cancellationToken);

    /// <summary>The names of the repository's configured remotes, read with a plain <c>git remote</c>.</summary>
    public Task<IReadOnlyList<string>> ReadRemoteNamesAsync(string repository, CancellationToken cancellationToken = default) =>
        this.reader.ReadRemotesAsync(repository, cancellationToken);


    /// <summary>Checks <paramref name="directory"/> through the path policy and starts one blob reader in its worktree. The caller disposes it.</summary>
    public Task<GitBlobReader> OpenBlobReaderAsync(string directory, CancellationToken cancellationToken = default) =>
        this.reader.OpenBlobReaderAsync(directory, cancellationToken);
}
