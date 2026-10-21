namespace Darkspyre.Common.Git;

/// <summary>What removing or resetting a worktree would lose.</summary>
/// <param name="HeadCommit">The commit HEAD names, or null when the path is not a worktree or HEAD has no commit.</param>
/// <param name="BranchRef">The full ref of the checked-out branch, or null when HEAD is detached or the path is not a worktree.</param>
/// <param name="TrackedChanges">Paths of tracked files with uncommitted changes.</param>
/// <param name="UntrackedFiles">Paths of untracked files.</param>
/// <param name="IgnoredFiles">Paths of ignored files.</param>
/// <param name="UnpreservedCommits">Commits reachable from HEAD that no retained shared ref reaches.</param>
/// <param name="Blockers">Why the worktree cannot be removed without loss. Empty when nothing blocks removal.</param>
/// <param name="CanRemoveWithoutLoss">True when <paramref name="Blockers"/> is empty.</param>
/// <param name="CanResetWithoutLoss">True when nothing but ignored files blocks removal.</param>
public sealed record GitPreservationCheck(string? HeadCommit, string? BranchRef,
    IReadOnlyList<string> TrackedChanges, IReadOnlyList<string> UntrackedFiles, IReadOnlyList<string> IgnoredFiles,
    IReadOnlyList<string> UnpreservedCommits, IReadOnlyList<string> Blockers, bool CanRemoveWithoutLoss, bool CanResetWithoutLoss)
{
    /// <summary>The blocker a preservation check reports for ignored files, such as build output.</summary>
    public const string IgnoredFilesBlocker = "Ignored files are not preserved.";
}
