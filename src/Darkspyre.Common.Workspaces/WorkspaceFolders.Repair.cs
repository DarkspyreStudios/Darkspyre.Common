using Darkspyre.Common.Git;

namespace Darkspyre.Common.Workspaces;

public sealed partial class WorkspaceFolders
{
    /// <summary>
    /// Makes a workspace folder whole again without losing anything: it stashes each healthy worktree's changes, makes every
    /// configured repository a locked worktree on the branch, moves every other top-level item into
    /// <c>tmp/repaired-&lt;yyyyMMdd-HHmmss&gt;</c>, and re-applies the stashes.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>Each healthy worktree's uncommitted and untracked changes are stashed with the message
    /// <c>darkspyre-repair &lt;timestamp&gt;</c>, and the stash commit is recorded.</item>
    /// <item>A broken repository folder is moved into the repair folder. The record of each missing or broken worktree is
    /// unlocked, and each repository's records of worktrees whose folders are gone are pruned. A missing or broken worktree is re-created on the branch: the local branch when it
    /// exists, else a new branch at <c>origin/</c> the branch, else a new branch at the repository's base, fetched first as
    /// a creation fetches it. Every configured worktree is locked; a locked one keeps its lock.</item>
    /// <item>Every stray top-level item, folder or file, is moved into the repair folder under its own name. A git
    /// worktree that is not a configured repository, such as one made for a single task, is left where it is and
    /// reported. Nothing is deleted.</item>
    /// <item>Each stash is re-applied by its commit and dropped only when it applied cleanly. A conflicting re-apply leaves
    /// the stash and the worktree in its conflicted state, and the result names the repository, the paths and the stash
    /// commit.</item>
    /// </list>
    /// A worktree whose re-creation fails stops the repair with the failure; the stashes already taken stay in their
    /// repositories' stash lists.
    /// </remarks>
    public async Task<WorkspaceRepairResult> RepairAsync(WorkspaceRepairRequest request, WorkspaceActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        string folder = FolderOf(request.Folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BranchName);
        string timestamp = this.Timestamp();
        Directory.CreateDirectory(folder);
        WorkspaceInspection inspection = await this.InspectAsync(folder, request.Repositories, cancellationToken).ConfigureAwait(false);
        string repaired = Path.Combine(folder, TemporaryFolderName, "repaired-" + timestamp);

        // 1. Save each healthy worktree's changes.
        List<(WorkspaceRepositoryStatus Status, string Commit)> stashes = [];
        foreach (WorkspaceRepositoryStatus status in inspection.Repositories.Where(item => item.State == WorkspaceRepositoryState.Healthy))
        {
            Log.Stashing(this.logger, status.Path, actor.Actor, actor.Trigger);
            GitStashSaveResult saved = await this.git.SaveStashAsync(new(status.Path, "darkspyre-repair " + timestamp, IncludeUntracked: true),
                cancellationToken).ConfigureAwait(false);
            if (saved.StashCommit is { } commit) stashes.Add((status, commit));
        }

        // 2. Make every configured repository a locked worktree on the branch.
        List<WorkspaceMovedItem> moved = [];
        foreach (WorkspaceRepositoryStatus status in inspection.Repositories.Where(item => item.State == WorkspaceRepositoryState.Broken))
            moved.Add(this.MoveAside(status.Path, repaired, actor));
        WorkspaceRepositoryStatus[] absent = [.. inspection.Repositories.Where(item => item.State != WorkspaceRepositoryState.Healthy)];
        // Prune keeps a locked record, and its branch stays checked out there, so a deleted worktree's record is unlocked first.
        foreach (WorkspaceRepositoryStatus status in absent)
        {
            Log.Unlocking(this.logger, status.Path, actor.Actor, actor.Trigger);
            await this.git.UnlockWorktreeRecordAsync(status.Repository.SourceRepository, status.Path, cancellationToken).ConfigureAwait(false);
        }
        foreach (string source in absent.Select(item => item.Repository.SourceRepository).Distinct(StringComparer.Ordinal))
        {
            Log.Pruning(this.logger, source, actor.Actor, actor.Trigger);
            await this.git.PruneWorktreesAsync(source, cancellationToken).ConfigureAwait(false);
        }
        List<WorkspaceRecreatedWorktree> recreated = [];
        foreach (WorkspaceRepositoryStatus status in absent)
        {
            WorkspaceRepository repository = status.Repository;
            await this.FetchBaseAsync(repository, actor, cancellationToken).ConfigureAwait(false);
            Log.RecreatingWorktree(this.logger, status.Path, request.BranchName, actor.Actor, actor.Trigger);
            GitWorktreeAttachResult attached = await this.git.AttachWorktreeAsync(
                new(repository.SourceRepository, status.Path, request.BranchName, repository.BaseRef), cancellationToken).ConfigureAwait(false);
            recreated.Add(new(new(repository, status.Path, attached.Worktree), attached.Source));
        }
        foreach (WorkspaceRepositoryStatus status in inspection.Repositories)
            await this.LockAsync(status.Path, actor, cancellationToken).ConfigureAwait(false);

        // 3. Move every stray item out of the way.
        foreach (string stray in inspection.Strays) moved.Add(this.MoveAside(stray, repaired, actor));

        // 4. Put the saved changes back.
        List<string> reapplied = [];
        List<WorkspaceStashConflict> conflicts = [];
        foreach ((WorkspaceRepositoryStatus status, string commit) in stashes)
        {
            Log.Reapplying(this.logger, commit, status.Path, actor.Actor, actor.Trigger);
            GitStashApplyResult applied;
            try
            {
                applied = await this.git.ApplyStashAsync(new(status.Path, commit), cancellationToken).ConfigureAwait(false);
            }
            catch (GitOperationException error) when (error.Failure == GitFailure.CommandFailed)
            {
                Log.StashKept(this.logger, commit, status.Path, error.Message);
                conflicts.Add(new(status.Repository, commit, []));
                continue;
            }
            if (applied.State == GitSequenceState.Completed)
            {
                await this.git.DropStashAsync(new(status.Path, commit), cancellationToken).ConfigureAwait(false);
                reapplied.Add(commit);
            }
            else
            {
                Log.StashKept(this.logger, commit, status.Path, string.Join(", ", applied.ConflictedPaths));
                conflicts.Add(new(status.Repository, commit, applied.ConflictedPaths));
            }
        }
        return new(folder, recreated.AsReadOnly(), moved.AsReadOnly(), moved.Count > 0 ? repaired : null, reapplied.AsReadOnly(),
            conflicts.AsReadOnly(), inspection.OtherWorktrees);
    }

    // Moves a top-level item into the repair folder under its own name.
    private WorkspaceMovedItem MoveAside(string item, string repaired, WorkspaceActor actor)
    {
        Directory.CreateDirectory(repaired);
        string destination = Path.Combine(repaired, Path.GetFileName(item));
        Log.Moving(this.logger, item, destination, actor.Actor, actor.Trigger);
        if (Directory.Exists(item)) Directory.Move(item, destination);
        else File.Move(item, destination);
        return new(item, destination);
    }
}
