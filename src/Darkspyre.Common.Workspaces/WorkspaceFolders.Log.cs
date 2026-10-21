using Microsoft.Extensions.Logging;

namespace Darkspyre.Common.Workspaces;

public sealed partial class WorkspaceFolders
{
    // One Information line per step, naming the path, the acting owner and the trigger, so every change to a workspace
    // can be attributed afterwards.
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Creating the workspace folder {Folder} for the {Trigger} of {Actor}")]
        internal static partial void CreatingFolder(ILogger logger, string folder, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Fetching {Base} in {Repository} for the {Trigger} of {Actor}")]
        internal static partial void Fetching(ILogger logger, string @base, string repository, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Creating the worktree {Worktree} on the new branch {Branch} from {Base} for the {Trigger} of {Actor}")]
        internal static partial void CreatingWorktree(ILogger logger, string worktree, string branch, string @base, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Skipping the repository {Name} for the {Trigger} of {Actor}: {Reason}")]
        internal static partial void Skipping(ILogger logger, string name, string reason, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Locking the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void Locking(ILogger logger, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unlocking the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void Unlocking(ILogger logger, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Removing the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void RemovingWorktree(ILogger logger, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Deleting the branch {Branch} at {Commit} of the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void DeletingBranch(ILogger logger, string branch, string commit, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Deleting the workspace folder {Folder} for the {Trigger} of {Actor}")]
        internal static partial void DeletingFolder(ILogger logger, string folder, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Workspace rollback kept the worktree {Worktree} and its branch {Branch}: {Status} {Reason}")]
        internal static partial void UndoKept(ILogger logger, string worktree, string branch, string status, string? reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Workspace rollback could not remove the worktree {Worktree} or its branch {Branch}")]
        internal static partial void UndoFailed(ILogger logger, Exception error, string worktree, string branch);

        [LoggerMessage(Level = LogLevel.Error, Message = "Workspace rollback could not delete the workspace folder {Folder}")]
        internal static partial void FolderDeleteFailed(ILogger logger, Exception error, string folder);

        [LoggerMessage(Level = LogLevel.Information, Message = "Stashing the changes of the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void Stashing(ILogger logger, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pruning the worktree records of {Repository} for the {Trigger} of {Actor}")]
        internal static partial void Pruning(ILogger logger, string repository, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Re-creating the worktree {Worktree} on the branch {Branch} for the {Trigger} of {Actor}")]
        internal static partial void RecreatingWorktree(ILogger logger, string worktree, string branch, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Moving {Item} to {Destination} for the {Trigger} of {Actor}")]
        internal static partial void Moving(ILogger logger, string item, string destination, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Re-applying the stash {Stash} in the worktree {Worktree} for the {Trigger} of {Actor}")]
        internal static partial void Reapplying(ILogger logger, string stash, string worktree, string actor, string trigger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Kept the stash {Stash} in the worktree {Worktree}: {Reason}")]
        internal static partial void StashKept(ILogger logger, string stash, string worktree, string reason);
    }
}
