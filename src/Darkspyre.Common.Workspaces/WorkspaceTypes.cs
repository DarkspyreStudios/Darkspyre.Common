using Darkspyre.Common.Git;

namespace Darkspyre.Common.Workspaces;

/// <summary>One repository a workspace holds a worktree of.</summary>
/// <param name="Name">The worktree's folder name inside the workspace folder.</param>
/// <param name="SourceRepository">A folder inside the repository the worktree belongs to.</param>
/// <param name="BaseRef">
/// The commit or ref a new branch starts at. A remote-tracking branch, <c>&lt;remote&gt;/&lt;branch&gt;</c>, is fetched
/// first.
/// </param>
public sealed record WorkspaceRepository(string Name, string SourceRepository, string BaseRef);

/// <summary>Who a workspace operation acts for and why, as its log lines name them.</summary>
/// <param name="Actor">The acting owner, such as <c>session 0f3c…</c> or <c>worker 9a1b…</c>.</param>
/// <param name="Trigger">What started the operation, such as <c>session delete</c>, <c>worker delete</c> or <c>repair</c>.</param>
public sealed record WorkspaceActor(string Actor, string Trigger);

/// <summary>A request to create a workspace folder.</summary>
/// <param name="Folder">The absolute workspace folder. It must not exist yet.</param>
/// <param name="BranchName">The new branch every worktree is created on. It must not exist in any of the repositories.</param>
/// <param name="Repositories">The repositories, in order. Their names must differ from each other and from <c>tmp</c>.</param>
/// <param name="SkipFailures">
/// True to skip a repository whose fetch or worktree fails and go on; false to undo everything on the first failure and
/// throw it.
/// </param>
public sealed record WorkspaceCreateRequest(string Folder, string BranchName, IReadOnlyList<WorkspaceRepository> Repositories,
    bool SkipFailures);

/// <summary>A repository worktree in a workspace.</summary>
/// <param name="Repository">The repository it is a worktree of.</param>
/// <param name="Path">The worktree's folder: <c>&lt;workspace&gt;/&lt;name&gt;</c>.</param>
/// <param name="Worktree">The worktree as Git created or attached it.</param>
public sealed record WorkspaceWorktree(WorkspaceRepository Repository, string Path, GitWorktreeSnapshot Worktree);

/// <summary>A repository a creation skipped, and why.</summary>
/// <param name="Repository">The repository.</param>
/// <param name="Reason">The failure's message, without a final period.</param>
public sealed record WorkspaceSkippedRepository(WorkspaceRepository Repository, string Reason);

/// <summary>What a creation made.</summary>
/// <param name="Folder">The workspace folder. It no longer exists when <see cref="Worktrees"/> is empty.</param>
/// <param name="Worktrees">The worktrees made, in request order.</param>
/// <param name="Skipped">The repositories skipped, in request order; empty unless the request skips failures.</param>
public sealed record WorkspaceCreateResult(string Folder, IReadOnlyList<WorkspaceWorktree> Worktrees,
    IReadOnlyList<WorkspaceSkippedRepository> Skipped);

/// <summary>The state of one configured repository in a workspace.</summary>
public enum WorkspaceRepositoryState
{
    /// <summary>Its folder is a worktree's own folder of the repository, locked or not.</summary>
    Healthy,
    /// <summary>Nothing has its name in the workspace folder.</summary>
    Missing,
    /// <summary>Its folder exists but is not a worktree's own folder of the repository.</summary>
    Broken,
}

/// <summary>One configured repository as an inspection found it.</summary>
/// <param name="Repository">The repository.</param>
/// <param name="Path">Where its worktree belongs: <c>&lt;workspace&gt;/&lt;name&gt;</c>.</param>
/// <param name="State">What is there.</param>
/// <param name="BranchRef">The checked-out branch of a healthy worktree; null otherwise or when HEAD is detached.</param>
public sealed record WorkspaceRepositoryStatus(WorkspaceRepository Repository, string Path, WorkspaceRepositoryState State,
    string? BranchRef);

/// <summary>What a workspace folder holds.</summary>
/// <param name="Folder">The workspace folder.</param>
/// <param name="Repositories">Each configured repository, in configuration order.</param>
/// <param name="Strays">
/// Every top-level item, folder or file, that is neither a configured repository's name, nor <c>tmp</c>, nor another
/// worktree, sorted ordinally.
/// </param>
/// <param name="OtherWorktrees">
/// Every top-level folder that is a git worktree's own folder but not a configured repository's name, such as a worktree
/// made for one task inside the workspace, sorted ordinally. A repair leaves each where it is.
/// </param>
public sealed record WorkspaceInspection(string Folder, IReadOnlyList<WorkspaceRepositoryStatus> Repositories,
    IReadOnlyList<string> Strays, IReadOnlyList<string> OtherWorktrees);

/// <summary>What a removal removed.</summary>
/// <param name="Path">The removed workspace folder or worktree.</param>
/// <param name="Worktrees">The worktrees removed.</param>
/// <param name="Branches">The full refs of the branches deleted.</param>
public sealed record WorkspaceRemovalResult(string Path, IReadOnlyList<string> Worktrees, IReadOnlyList<string> Branches);

/// <summary>A request to repair a workspace folder.</summary>
/// <param name="Folder">The absolute workspace folder.</param>
/// <param name="BranchName">The branch a re-created worktree checks out.</param>
/// <param name="Repositories">The configured repositories.</param>
public sealed record WorkspaceRepairRequest(string Folder, string BranchName, IReadOnlyList<WorkspaceRepository> Repositories);

/// <summary>A repository whose stash a repair could not re-apply cleanly.</summary>
/// <param name="Repository">The repository.</param>
/// <param name="StashCommit">The stash's commit, still in the repository's stash list.</param>
/// <param name="ConflictedPaths">The paths left unmerged in the worktree.</param>
public sealed record WorkspaceStashConflict(WorkspaceRepository Repository, string StashCommit, IReadOnlyList<string> ConflictedPaths);

/// <summary>A worktree a repair re-created, and where its branch came from.</summary>
/// <param name="Worktree">The worktree.</param>
/// <param name="Source">The local branch, the branch on <c>origin</c>, or a new branch from the base.</param>
public sealed record WorkspaceRecreatedWorktree(WorkspaceWorktree Worktree, GitWorktreeAttachSource Source);

/// <summary>An item a repair moved out of the way.</summary>
/// <param name="From">Its old path.</param>
/// <param name="To">Its new path, inside the repair's folder.</param>
public sealed record WorkspaceMovedItem(string From, string To);

/// <summary>What a repair did.</summary>
/// <param name="Folder">The workspace folder.</param>
/// <param name="Recreated">The worktrees re-created, with where each branch came from.</param>
/// <param name="Moved">Each item moved, from its old path to its new path.</param>
/// <param name="RepairedFolder">The folder the items were moved into, or null when nothing was moved.</param>
/// <param name="Reapplied">The stash commits re-applied and dropped.</param>
/// <param name="Conflicts">The stashes left in place because re-applying them conflicted.</param>
/// <param name="OtherWorktrees">The worktrees that are not configured repositories, which the repair left where they are.</param>
public sealed record WorkspaceRepairResult(string Folder, IReadOnlyList<WorkspaceRecreatedWorktree> Recreated,
    IReadOnlyList<WorkspaceMovedItem> Moved, string? RepairedFolder, IReadOnlyList<string> Reapplied,
    IReadOnlyList<WorkspaceStashConflict> Conflicts, IReadOnlyList<string> OtherWorktrees);
