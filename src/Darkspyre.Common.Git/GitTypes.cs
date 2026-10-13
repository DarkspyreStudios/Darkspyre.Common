namespace Darkspyre.Common.Git;

/// <summary>One entry of the working tree's porcelain status, including untracked and ignored files.</summary>
/// <param name="Path">The repository-relative path.</param>
/// <param name="IndexStatus">Git's one-character status code for the index.</param>
/// <param name="WorktreeStatus">Git's one-character status code for the working tree.</param>
/// <param name="OriginalPath">The source path of a rename or copy, or null.</param>
/// <param name="IsUntracked">True when Git does not track the path.</param>
/// <param name="IsIgnored">True when the path is ignored.</param>
public sealed record GitStatusEntry(string Path, string IndexStatus, string WorktreeStatus,
    string? OriginalPath = null, bool IsUntracked = false, bool IsIgnored = false);
/// <summary>The identity and status of one Git working tree.</summary>
/// <param name="CommonDirectory">The absolute Git common directory shared by all worktrees of the repository.</param>
/// <param name="WorktreePath">The absolute root of the working tree.</param>
/// <param name="HeadCommit">The commit HEAD names, or null when the branch has no commits.</param>
/// <param name="BranchRef">The full ref of the checked-out branch, or null when HEAD is detached.</param>
/// <param name="Status">The working tree's status entries.</param>
/// <param name="GitDirectory">The absolute administrative directory containing this working tree's HEAD and index.</param>
public sealed record GitRepositorySnapshot(string CommonDirectory, string WorktreePath,
    string? HeadCommit, string? BranchRef, IReadOnlyList<GitStatusEntry> Status, string GitDirectory)
{
    /// <summary>The repository's object format, which sets the width of its object IDs.</summary>
    public GitObjectFormat ObjectFormat { get; init; }
}
/// <summary>A request to clone a remote repository.</summary>
/// <param name="RemoteUrl">The URL Git clones from.</param>
/// <param name="DestinationPath">The absolute destination. It must not already exist.</param>
/// <param name="Ref">An optional branch, tag, full ref or commit ID to check out. A full ref or commit ID produces a detached HEAD.</param>
public sealed record GitCloneRequest(string RemoteUrl, string DestinationPath, string? Ref = null);
/// <summary>Exact source refs or source:full-destination-ref mappings. Source-only fetches do not implicitly update tracking refs.</summary>
/// <param name="RepositoryPath">A directory inside the repository.</param>
/// <param name="RemoteName">A configured remote to fetch from.</param>
/// <param name="Refs">The source refs or <c>source:destination</c> mappings to fetch. At least one is required.</param>
public sealed record GitFetchRequest(string RepositoryPath, string RemoteName, IReadOnlyList<string> Refs);
/// <summary>One ref a fetch changed.</summary>
/// <param name="Ref">The full name of the updated ref.</param>
/// <param name="PreviousCommit">The commit the ref named before the fetch, or null when the fetch created it.</param>
/// <param name="CurrentCommit">The commit the ref names after the fetch.</param>
public sealed record GitRefUpdate(string Ref, string? PreviousCommit, string? CurrentCommit);
/// <summary>
/// The refs a fetch changed, read by comparing the destination refs before and after it. A failure diagnostic requires
/// reinspection, not an assumption of no effects or permission to retry.
/// </summary>
/// <param name="RemoteName">The remote the fetch used.</param>
/// <param name="UpdatedRefs">The destination refs whose commit changed, with the commit before and after.</param>
/// <param name="Diagnostic">Why the fetch did not complete successfully, or null when it did.</param>
public sealed record GitFetchResult(string RemoteName, IReadOnlyList<GitRefUpdate> UpdatedRefs, string? Diagnostic = null);
/// <summary>The two sides a diff compares.</summary>
public abstract record GitComparison;
/// <summary>Compares two commits. A diff result carries both refs resolved to commit IDs.</summary>
/// <param name="FromRef">The commit or ref on the old side.</param>
/// <param name="ToRef">The commit or ref on the new side.</param>
public sealed record CommitToCommit(string FromRef, string ToRef) : GitComparison;
/// <summary>Compares the index with the working tree, showing unstaged changes.</summary>
public sealed record IndexToWorktree : GitComparison;
/// <summary>Compares HEAD with the index, showing staged changes.</summary>
public sealed record HeadToIndex : GitComparison;
/// <summary>
/// A request to read a diff. The result holds only the files the caller's current scope may read: a refused file's name
/// and changes are left out without a count.
/// </summary>
/// <param name="RepositoryPath">A directory inside the repository.</param>
/// <param name="Comparison">The two sides to compare.</param>
/// <param name="Paths">
/// Optional literal repository-relative paths that limit the diff, or null for every path. A path the caller's scope
/// refuses throws <see cref="UnauthorizedAccessException"/> before Git runs.
/// </param>
public sealed record GitDiffRequest(string RepositoryPath, GitComparison Comparison, IReadOnlyList<string>? Paths = null);
/// <summary>One file in a diff.</summary>
/// <param name="Path">The file's path on the new side.</param>
/// <param name="OriginalPath">The file's path on the old side when it was renamed, or null.</param>
/// <param name="IsBinary">True when Git reports no line counts because the file is binary.</param>
/// <param name="IsRename">True when Git detected a rename.</param>
public sealed record GitChangedFile(string Path, string? OriginalPath, bool IsBinary, bool IsRename);

/// <summary>Caller-owned untruncated raw patch; disposing the result disposes its stream.</summary>
/// <param name="ResolvedComparison">The comparison the diff used. A <see cref="CommitToCommit"/> carries both refs resolved to commit IDs.</param>
/// <param name="Files">The files the diff changes.</param>
/// <param name="Patch">The raw patch text, untruncated.</param>
public sealed record GitDiffResult(GitComparison ResolvedComparison, IReadOnlyList<GitChangedFile> Files,
    Stream Patch) : IAsyncDisposable
{
    /// <summary>Disposes the patch stream.</summary>
    public ValueTask DisposeAsync() => Patch.DisposeAsync();
}

/// <summary>A literal repository-relative path at a commit or ref, and the largest file the caller accepts.</summary>
/// <param name="RepositoryPath">A directory inside the repository.</param>
/// <param name="Reference">The commit or ref to read the file at.</param>
/// <param name="Path">The literal repository-relative path, with forward slashes.</param>
/// <param name="MaximumBytes">The largest file accepted, in bytes, from 0 to <see cref="Array.MaxLength"/>. The bytes are held in memory in one array, so the caller sets the bound; a value outside that range throws <see cref="ArgumentException"/>.</param>
public sealed record GitFileAtCommitRequest(string RepositoryPath, string Reference, string Path, long MaximumBytes);

/// <summary>A committed file's exact bytes, held in memory. An absent path has no mode and no content.</summary>
/// <param name="CommitId">The commit the reference resolved to.</param>
/// <param name="Present">True when the path exists at that commit.</param>
/// <param name="Mode">The file's Git mode, such as <c>100644</c>, or null when the path is absent.</param>
/// <param name="Content">The file's bytes as a read-only stream over memory, or null when the path is absent.</param>
public sealed record GitFileContent(string CommitId, bool Present, string? Mode, Stream? Content) : IAsyncDisposable
{
    /// <summary>Disposes the content stream, if any.</summary>
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>A request to list one commit's entries.</summary>
/// <param name="RepositoryPath">A directory inside the repository.</param>
/// <param name="CommitId">The full commit ID, as a resolution returned it.</param>
/// <param name="Folder">A repository-relative folder without surrounding slashes, or empty for the whole tree.</param>
public sealed record GitTreeRequest(string RepositoryPath, string CommitId, string Folder);

/// <summary>One leaf of a commit's tree. No entry is a directory.</summary>
/// <param name="Path">The repository-relative path as Git stores it.</param>
/// <param name="Kind">The entry's kind.</param>
/// <param name="Mode">Git's octal mode text, such as <c>100644</c>.</param>
/// <param name="ObjectId">The blob, or the commit of a submodule.</param>
/// <param name="Length">The blob's length in bytes, or null for a submodule.</param>
public sealed record GitTreeEntry(string Path, GitEntryKind Kind, string Mode, string ObjectId, long? Length);

/// <summary>
/// A request to push one commit to one remote branch. A normal push never forces; a lease push replaces the remote
/// branch only while it still names the expected commit. The host never retries a push.
/// </summary>
/// <param name="RepositoryPath">A directory inside the repository.</param>
/// <param name="RemoteName">A configured remote with exactly one push URL.</param>
/// <param name="Source">The commit or ref to push. It is resolved to a commit ID before the push.</param>
/// <param name="DestinationBranchRef">The full destination branch ref. It must start with <c>refs/heads/</c>.</param>
/// <param name="Mode">Whether the push may replace the remote branch under a lease.</param>
/// <param name="ExpectedRemoteOid">
/// For <see cref="GitPushMode.Lease"/>, the commit the remote branch must name, or null when it must be absent. A normal
/// push must leave it null.
/// </param>
public sealed record GitPushRequest(string RepositoryPath, string RemoteName, string Source, string DestinationBranchRef,
    GitPushMode Mode = GitPushMode.Normal, string? ExpectedRemoteOid = null);
/// <summary>How a push may update the remote branch.</summary>
public enum GitPushMode
{
    /// <summary>Only a fast-forward or a new branch; never a forced update.</summary>
    Normal,
    /// <summary>
    /// Any update, including a forced one, while the remote branch still names
    /// <see cref="GitPushRequest.ExpectedRemoteOid"/> (<c>--force-with-lease</c> with an explicit expected value).
    /// </summary>
    Lease,
}
/// <summary>What the remote did with a pushed ref.</summary>
public enum GitPushOutcome
{
    /// <summary>The remote accepted the ref update.</summary>
    Accepted,
    /// <summary>The remote rejected the ref update.</summary>
    Rejected,
    /// <summary>Remote acceptance was not confirmed. The caller must inspect remote state before another action.</summary>
    Unknown,
}
/// <summary>The outcome for one pushed destination ref.</summary>
/// <param name="DestinationRef">The full destination ref.</param>
/// <param name="Outcome">What the remote did with the ref.</param>
/// <param name="Diagnostic">A host-written explanation, or null. It never contains remote hook output.</param>
public sealed record GitPushRefResult(string DestinationRef, GitPushOutcome Outcome, string? Diagnostic = null);
/// <summary>The outcome of a push.</summary>
/// <param name="Refs">The outcome for each destination ref.</param>
/// <param name="Diagnostic">A host-written explanation of a rejected, unconfirmed or unclean push, or null.</param>
public sealed record GitPushResult(IReadOnlyList<GitPushRefResult> Refs, string? Diagnostic = null);
/// <summary>A request to create a linked worktree on a new branch.</summary>
/// <param name="RepositoryPath">A directory inside the source repository.</param>
/// <param name="WorktreePath">The absolute path of the new worktree. It must not already exist.</param>
/// <param name="SourceRef">The commit or ref the new branch starts at.</param>
/// <param name="BranchName">The short name of the new branch. It must not already exist.</param>
public sealed record GitWorktreeRequest(string RepositoryPath, string WorktreePath, string SourceRef, string BranchName);
/// <summary>A linked worktree the host created.</summary>
/// <param name="CommonDirectory">The absolute Git common directory of the repository.</param>
/// <param name="WorktreePath">The absolute root of the new worktree.</param>
/// <param name="BranchRef">The full ref of the new branch.</param>
/// <param name="BaseCommit">The commit the branch was created at.</param>
public sealed record GitWorktreeSnapshot(string CommonDirectory, string WorktreePath, string BranchRef, string BaseCommit);
/// <summary>A request to commit explicit paths on the checked-out branch.</summary>
/// <param name="WorktreePath">The worktree root. A subdirectory is rejected.</param>
/// <param name="Paths">
/// The files or entries to stage and commit. It must not be empty or name the whole tree, Git metadata or a directory.
/// </param>
/// <param name="Message">The commit message.</param>
public sealed record GitCommitRequest(string WorktreePath, IReadOnlyList<string> Paths, string Message);
/// <summary>A commit Git made.</summary>
/// <param name="CommitId">The commit HEAD names after Git exited.</param>
/// <param name="BranchRef">The full ref of the branch the commit was made on.</param>
public sealed record GitCommitResult(string CommitId, string BranchRef);
/// <summary>The outcome of a worktree removal.</summary>
public enum GitRemovalStatus
{
    /// <summary>Git removed the worktree.</summary>
    Removed,
    /// <summary>The worktree was kept because removal would lose work, its state changed, or the scope does not permit removal.</summary>
    Retained,
    /// <summary>Git did not confirm the removal.</summary>
    Failed,
}
/// <summary>The outcome of a worktree removal.</summary>
/// <param name="Status">Whether the worktree was removed, retained or failed to be removed.</param>
/// <param name="Preservation">The preservation check the decision was based on.</param>
/// <param name="RemainingPath">The worktree path when it still exists, or null.</param>
/// <param name="BranchRef">The full ref of the worktree's branch, which removal never deletes.</param>
/// <param name="Diagnostic">The reason for a retained or failed removal, or null.</param>
public sealed record GitRemovalResult(GitRemovalStatus Status, GitPreservationCheck Preservation,
    string? RemainingPath, string? BranchRef, string? Diagnostic = null);
/// <summary>The branch a worktree reset switches to.</summary>
public abstract record GitResetTarget;
/// <summary>Switches to a new branch created at a start ref.</summary>
/// <param name="BranchName">The short name of the new branch. It must not already exist.</param>
/// <param name="StartRef">The commit or ref the new branch starts at.</param>
public sealed record GitResetToNewBranch(string BranchName, string StartRef) : GitResetTarget;
/// <summary>Switches to an existing branch that no other worktree has checked out.</summary>
/// <param name="BranchName">The short name of the existing branch.</param>
public sealed record GitResetToExistingBranch(string BranchName) : GitResetTarget;
/// <summary>A request to reset a linked worktree onto another branch.</summary>
/// <param name="WorktreePath">The absolute worktree root.</param>
/// <param name="ExpectedHeadCommit">The commit HEAD must name; otherwise the reset is refused.</param>
/// <param name="Target">The branch to switch to.</param>
public sealed record GitResetRequest(string WorktreePath, string ExpectedHeadCommit, GitResetTarget Target);
/// <summary>The outcome of a worktree reset.</summary>
public enum GitResetStatus
{
    /// <summary>The worktree is on the target branch with no untracked files.</summary>
    Reset,
    /// <summary>The reset was not attempted because a precondition failed.</summary>
    Refused,
    /// <summary>Git did not confirm the switch or the clean; the worktree must be inspected before another action.</summary>
    Failed,
}
/// <summary>The outcome of a worktree reset.</summary>
/// <param name="Status">Whether the reset completed, was refused or failed.</param>
/// <param name="Preservation">The preservation check the decision was based on.</param>
/// <param name="HeadCommit">The commit HEAD names after the operation, or null.</param>
/// <param name="BranchRef">The full ref of the checked-out branch after the operation, or null.</param>
/// <param name="RemovedUntracked">The untracked paths the reset removed.</param>
/// <param name="Diagnostic">The reason for a refused or failed reset, or null.</param>
public sealed record GitResetResult(GitResetStatus Status, GitPreservationCheck Preservation,
    string? HeadCommit, string? BranchRef, IReadOnlyList<string> RemovedUntracked, string? Diagnostic = null);
/// <summary>A request to create a branch.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Name">The new branch's short name. It must not already exist.</param>
/// <param name="StartPoint">The commit or ref the branch starts at, such as <c>HEAD</c>.</param>
/// <param name="ExpectedHead">The commit HEAD must name, or null when the branch must have no commits yet.</param>
public sealed record GitBranchCreateRequest(string Repository, string Name, string StartPoint, string? ExpectedHead);
/// <summary>A request to delete a branch.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Name">The branch's short name.</param>
/// <param name="ExpectedCommit">The commit the branch must name.</param>
public sealed record GitBranchDeleteRequest(string Repository, string Name, string ExpectedCommit);
/// <summary>A created or deleted branch.</summary>
/// <param name="BranchRef">The branch's full ref.</param>
/// <param name="CommitId">The commit the branch was created at, or named when it was deleted.</param>
public sealed record GitBranchResult(string BranchRef, string CommitId);
/// <summary>A merge call.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Action">What the call does.</param>
/// <param name="Source">For a start, the revision to merge; otherwise null.</param>
/// <param name="Message">For a start, the merge commit's message; otherwise null.</param>
public sealed record GitMergeRequest(string Repository, GitSequenceAction Action, string? Source, string? Message);
/// <summary>A request to stash the worktree's changes.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Message">The stash's message.</param>
/// <param name="IncludeUntracked">Whether untracked files are stashed too.</param>
public sealed record GitStashSaveRequest(string Repository, string Message, bool IncludeUntracked);
/// <summary>A saved stash.</summary>
/// <param name="StashRef">The new stash's reflog name, <c>stash@{0}</c>, or null when there was nothing to stash.</param>
/// <param name="StashCommit">The new stash's commit, or null when there was nothing to stash.</param>
public sealed record GitStashSaveResult(string? StashRef, string? StashCommit);
/// <summary>One stash.</summary>
/// <param name="Index">The stash's position, 0 for the newest; <c>stash@{Index}</c> names it.</param>
/// <param name="StashCommit">The stash's commit.</param>
/// <param name="Message">The stash's message.</param>
/// <param name="Branch">The branch the stash was saved on, or null when HEAD was detached.</param>
public sealed record GitStashEntry(int Index, string StashCommit, string Message, string? Branch);
/// <summary>A request to apply a stash.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="StashCommit">The commit of the stash to apply.</param>
public sealed record GitStashApplyRequest(string Repository, string StashCommit);
/// <summary>The outcome of a stash apply.</summary>
/// <param name="State"><see cref="GitSequenceState.Completed"/>, or <see cref="GitSequenceState.Conflicted"/> with unmerged paths.</param>
/// <param name="ConflictedPaths">The unmerged paths the caller's scope may read.</param>
public sealed record GitStashApplyResult(GitSequenceState State, IReadOnlyList<string> ConflictedPaths);
/// <summary>A request to drop a stash.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="StashCommit">The commit of the stash to drop.</param>
public sealed record GitStashDropRequest(string Repository, string StashCommit);
/// <summary>The outcome of a stash drop.</summary>
/// <param name="Dropped">True when the stash was dropped.</param>
public sealed record GitStashDropResult(bool Dropped);
/// <summary>A request to restore worktree paths from a revision.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Revision">The commit or ref to restore from.</param>
/// <param name="Paths">1 to 64 worktree paths, absolute or relative to the worktree root, never Git metadata.</param>
public sealed record GitRestoreRequest(string Repository, string Revision, IReadOnlyList<string> Paths);
/// <summary>The outcome of a restore.</summary>
/// <param name="CommitId">The commit the revision resolved to.</param>
/// <param name="RestoredPaths">The repository-relative paths written from the commit.</param>
/// <param name="MissingPaths">The repository-relative paths the commit does not hold; nothing was written for them.</param>
public sealed record GitRestoreResult(string CommitId, IReadOnlyList<string> RestoredPaths, IReadOnlyList<string> MissingPaths);

/// <summary>One changed path in a working tree, from Git's porcelain v2 status.</summary>
/// <param name="Path">The repository-relative path.</param>
/// <param name="OriginalPath">The path before a rename or copy, or null.</param>
/// <param name="IndexStatus">Git's status character for the index, <c>.</c> when unmodified; <c>?</c> for an untracked file.</param>
/// <param name="WorktreeStatus">Git's status character for the working tree, <c>.</c> when unmodified; <c>?</c> for an untracked file.</param>
public sealed record GitChange(string Path, string? OriginalPath, char IndexStatus, char WorktreeStatus);
/// <summary>The upstream of the checked-out branch, as local refs record it.</summary>
/// <param name="Ref">The upstream's full ref, such as <c>refs/remotes/origin/main</c>.</param>
/// <param name="Oid">The commit the upstream ref names, or null when the ref no longer exists locally ("gone").</param>
/// <param name="Ahead">Commits on the branch that the upstream lacks, or null when the upstream is gone.</param>
/// <param name="Behind">Commits on the upstream that the branch lacks, or null when the upstream is gone.</param>
public sealed record GitUpstream(string Ref, string? Oid, int? Ahead, int? Behind);
/// <summary>An operation in progress in a working tree.</summary>
/// <param name="Kind">One of <c>rebase</c>, <c>am</c>, <c>merge</c>, <c>cherry-pick</c> or <c>revert</c>.</param>
/// <param name="ConflictedPaths">The unmerged paths the caller's scope may read.</param>
public sealed record GitOperationState(string Kind, IReadOnlyList<string> ConflictedPaths);
/// <summary>A working tree's state as one status read observed it.</summary>
/// <param name="CommonDirectory">The absolute Git common directory.</param>
/// <param name="Worktree">The absolute root of the working tree.</param>
/// <param name="Head">The commit HEAD names, or null when the branch has no commits.</param>
/// <param name="BranchRef">The full ref of the checked-out branch, or null when HEAD is detached.</param>
/// <param name="Upstream">The branch's upstream, or null when it has none.</param>
/// <param name="Changes">The changed and untracked paths the caller's scope may read, each untracked file listed alone.</param>
/// <param name="Operation">The operation in progress, or null.</param>
/// <param name="ObservedAt">When Git's status command returned, in UTC.</param>
public sealed record GitStatusSnapshot(
    string CommonDirectory, string Worktree, string? Head, string? BranchRef, GitUpstream? Upstream,
    IReadOnlyList<GitChange> Changes, GitOperationState? Operation, DateTimeOffset ObservedAt);
/// <summary>A request for one page of history.</summary>
/// <param name="Repository">A directory inside the repository.</param>
/// <param name="Revision">The revision to walk from.</param>
/// <param name="Paths">Repository-relative paths; when not empty, only commits that touch one of them are listed.</param>
/// <param name="Limit">The most commits to return, at least 1.</param>
/// <param name="Skip">How many commits of the same walk were already returned.</param>
public sealed record GitHistoryRequest(string Repository, string Revision, IReadOnlyList<string> Paths, int Limit, int Skip = 0);
/// <summary>One commit in a history page.</summary>
/// <param name="Oid">The commit ID.</param>
/// <param name="Parents">The parent commit IDs; empty for a root commit.</param>
/// <param name="AuthorName">The author's name. Bytes that are not UTF-8 become replacement characters.</param>
/// <param name="AuthorTime">When the commit was authored.</param>
/// <param name="Subject">The first line of the message. Bytes that are not UTF-8 become replacement characters.</param>
public sealed record GitCommitSummary(string Oid, IReadOnlyList<string> Parents, string AuthorName, DateTimeOffset AuthorTime, string Subject);
/// <summary>One page of history.</summary>
/// <param name="ResolvedOid">The commit the revision resolved to. Pass it as the next page's revision.</param>
/// <param name="Commits">The page's commits, newest first.</param>
/// <param name="HasMore">True when more commits follow this page.</param>
public sealed record GitHistoryPage(string ResolvedOid, IReadOnlyList<GitCommitSummary> Commits, bool HasMore);
/// <summary>A request to compare the ancestry of two revisions.</summary>
/// <param name="Repository">A directory inside the repository.</param>
/// <param name="Left">The first revision.</param>
/// <param name="Right">The second revision.</param>
public sealed record GitAncestryRequest(string Repository, string Left, string Right);
/// <summary>How two commits relate.</summary>
/// <param name="ResolvedLeft">The commit the left revision resolved to.</param>
/// <param name="ResolvedRight">The commit the right revision resolved to.</param>
/// <param name="MergeBases">Every best common ancestor; empty when the histories are unrelated.</param>
/// <param name="LeftAhead">Commits reachable from the left that the right does not reach.</param>
/// <param name="RightAhead">Commits reachable from the right that the left does not reach.</param>
/// <param name="LeftContainsRight">True when the left reaches every commit the right reaches.</param>
/// <param name="RightContainsLeft">True when the right reaches every commit the left reaches.</param>
public sealed record GitAncestryResult(string ResolvedLeft, string ResolvedRight, IReadOnlyList<string> MergeBases,
    int LeftAhead, int RightAhead, bool LeftContainsRight, bool RightContainsLeft);
/// <summary>A request to read refs from a remote's push destination.</summary>
/// <param name="Repository">A directory inside the repository.</param>
/// <param name="Remote">A configured remote with exactly one push URL.</param>
/// <param name="Refs">1 to 64 full ref names, each starting with <c>refs/</c>.</param>
public sealed record GitRemoteRefsRequest(string Repository, string Remote, IReadOnlyList<string> Refs);
/// <summary>One ref a remote read reported.</summary>
/// <param name="Ref">The full ref name, as requested.</param>
/// <param name="Oid">The commit the remote ref names, or null when the remote confirmed it absent.</param>
public sealed record GitRemoteRef(string Ref, string? Oid);
/// <summary>How a remote read ended.</summary>
public enum GitRemoteReadOutcome
{
    /// <summary>The remote answered; every requested ref is reported.</summary>
    Confirmed,
    /// <summary>Git could not read the remote. No ref is reported.</summary>
    Failed,
    /// <summary>The remote did not answer within 30 seconds. No ref is reported.</summary>
    TimedOut,
}
/// <summary>The result of a remote read.</summary>
/// <param name="ObservedAt">When the read returned, in UTC.</param>
/// <param name="Refs">One entry per requested ref, in request order; empty unless <paramref name="Outcome"/> is Confirmed.</param>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Diagnostic">A host-written explanation of a failed or timed-out read, or null.</param>
public sealed record GitRemoteRefsResult(DateTimeOffset ObservedAt, IReadOnlyList<GitRemoteRef> Refs,
    GitRemoteReadOutcome Outcome, string? Diagnostic = null);
/// <summary>A request to stage explicit paths.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Paths">The files or entries to stage, never a directory, <c>.</c> or Git metadata.</param>
public sealed record GitStageRequest(string Repository, IReadOnlyList<string> Paths);
/// <summary>What staging changed.</summary>
/// <param name="StagedPaths">The requested paths whose index entries now differ from HEAD.</param>
public sealed record GitStageResult(IReadOnlyList<string> StagedPaths);
/// <summary>What a rebase, cherry-pick or merge call does.</summary>
public enum GitSequenceAction
{
    /// <summary>Starts a new operation.</summary>
    Start,
    /// <summary>Continues the operation in progress after its conflicts are staged.</summary>
    Continue,
    /// <summary>Abandons the operation in progress and restores the state before it started.</summary>
    Abort,
}
/// <summary>A rebase call.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Action">What the call does.</param>
/// <param name="Upstream">For a start, the revision to rebase onto; otherwise null.</param>
public sealed record GitRebaseRequest(string Repository, GitSequenceAction Action, string? Upstream);
/// <summary>A cherry-pick call.</summary>
/// <param name="Repository">The worktree root.</param>
/// <param name="Action">What the call does.</param>
/// <param name="Commits">For a start, 1 to 64 commits to apply in this order; otherwise empty.</param>
public sealed record GitCherryPickRequest(string Repository, GitSequenceAction Action, IReadOnlyList<string> Commits);
/// <summary>Where a rebase, cherry-pick, merge or stash apply call left the working tree.</summary>
public enum GitSequenceState
{
    /// <summary>No operation is in progress and Git exited cleanly after a start or continue.</summary>
    Completed,
    /// <summary>The operation is in progress with unmerged paths, or a stash apply left unmerged paths.</summary>
    Conflicted,
    /// <summary>The operation is in progress with no unmerged paths, for example after a commit hook failed.</summary>
    Stopped,
    /// <summary>An abort exited cleanly and no operation is in progress.</summary>
    Aborted,
    /// <summary>Git or a hook refused to start: it failed, no operation is in progress, and HEAD and the working tree's changes are unchanged.</summary>
    Refused,
    /// <summary>The call was cancelled after Git started, or the state afterwards fits none of the others.</summary>
    Unknown,
}
/// <summary>The result of a rebase, cherry-pick or merge call.</summary>
/// <param name="Head">The commit HEAD names afterwards, or null when unknown.</param>
/// <param name="BranchRef">The checked-out branch afterwards, or null when HEAD is detached or unknown.</param>
/// <param name="ConflictedPaths">The unmerged paths the caller's scope may read.</param>
/// <param name="State">Where the call left the working tree.</param>
/// <param name="NewCommits">
/// The commits the operation has added, oldest first: for a cherry-pick the commits this call created, for a rebase
/// every commit between the rebase's base and HEAD, and for a merge the merge commit.
/// </param>
/// <param name="Diagnostic">A host-written explanation, or null. It never contains Git or hook output.</param>
public sealed record GitSequenceResult(string? Head, string? BranchRef, IReadOnlyList<string> ConflictedPaths, GitSequenceState State, IReadOnlyList<string> NewCommits, string? Diagnostic = null)
{
    /// <summary>
    /// For a cherry-pick, each source commit this call processed that produced no new commit because its changes were
    /// already in HEAD; empty otherwise.
    /// </summary>
    public IReadOnlyList<string> SkippedCommits { get; init; } = [];
}
/// <summary>Why a Git operation refused or failed.</summary>
public enum GitFailure
{
    /// <summary>The branch's or HEAD's expected commit differs, HEAD is detached for a commit, or no operation of the requested kind is in progress; nothing changed.</summary>
    StaleState,
    /// <summary>A sequence start needs no change but untracked files and no operation in progress, or a continue found unmerged paths; nothing changed.</summary>
    NotClean,
    /// <summary>A revision does not resolve to a commit; nothing changed.</summary>
    RevisionNotFound,
    /// <summary>Git exited with a failure after it started changing the repository.</summary>
    CommandFailed,
    /// <summary>A branch with the requested name already exists; nothing changed.</summary>
    BranchExists,
    /// <summary>The branch is checked out in a worktree; nothing was deleted.</summary>
    BranchCheckedOut,
    /// <summary>No stash has the requested commit; nothing changed.</summary>
    StashNotFound,
}
/// <summary>
/// A Git operation refused or failed for the classified reason <see cref="Failure"/> names. It derives from
/// <see cref="IOException"/>. The message is written by the host and never contains Git's output, a remote URL or a
/// credential.
/// </summary>
/// <param name="failure">Why the operation refused or failed.</param>
/// <param name="message">A safe description of the failure.</param>
public sealed class GitOperationException(GitFailure failure, string message) : IOException(message)
{
    /// <summary>Why the operation refused or failed.</summary>
    public GitFailure Failure { get; } = failure;
}

/// <summary>Read-only provisioning observation. It pins a commit, not a branch reservation or write authorization.</summary>
/// <param name="RepositoryPath">The source repository's worktree root.</param>
/// <param name="CommonDirectory">The repository's Git common directory.</param>
/// <param name="RequestedSourceRef">The ref the caller asked to start from.</param>
/// <param name="BaseCommit">The commit that ref resolved to.</param>
/// <param name="BranchName">The new branch's short name.</param>
public sealed record PreparedGitWorktree(string RepositoryPath, string CommonDirectory,
    string RequestedSourceRef, string BaseCommit, string BranchName);
