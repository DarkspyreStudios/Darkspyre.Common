using System.Globalization;
using Darkspyre.Common.Git;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Common.Workspaces;

/// <summary>
/// The one implementation of workspace folders: a folder holding one git worktree per repository, all on one branch,
/// each locked, and the folder <c>tmp</c> with <c>tmp/home</c>. It creates, inspects, removes and repairs them through
/// <see cref="GitClient"/>, and logs each step with the caller's <see cref="WorkspaceActor"/>. It holds no state of its own
/// and knows nothing of who uses a workspace.
/// </summary>
/// <gotcha>
/// Folder creation, moves and deletion run in this process, outside the Git client's path policy; the caller must be
/// allowed to write the workspace folder.
/// </gotcha>
public sealed partial class WorkspaceFolders
{
    /// <summary>The reason every workspace worktree is locked with. Git refuses to remove a locked worktree, and tools that clean up worktrees skip one.</summary>
    public const string LockReason = "Darkspyre session workspace; do not remove";

    /// <summary>The folder in a workspace for its temporary files. It is never a worktree.</summary>
    public const string TemporaryFolderName = "tmp";

    /// <summary>
    /// The folder in <see cref="TemporaryFolderName"/> for the user data of the workspace's build tools, such as the
    /// dotnet CLI home. The workspace's processes keep the account's home directory.
    /// </summary>
    public const string HomeFolderName = "home";

    private readonly GitClient git;
    private readonly IGitPathPolicy paths;
    private readonly ILogger logger;
    private readonly TimeProvider time;

    /// <summary>Creates the workspace operations over one Git client.</summary>
    /// <param name="git">Runs every Git operation.</param>
    /// <param name="paths">The Git client's path policy, which resolves paths the way Git reports them.</param>
    /// <param name="logger">Receives one Information line per step.</param>
    /// <param name="time">Names a repair's folder and stashes; the system clock when null.</param>
    public WorkspaceFolders(GitClient git, IGitPathPolicy paths, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.git = git;
        this.paths = paths;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates the workspace folder, then, for each repository in order, fetches its base when the base names a
    /// remote-tracking branch, creates its worktree at <c>&lt;folder&gt;/&lt;name&gt;</c> on the new branch, and locks it;
    /// then creates <c>tmp/home</c>. A repository with no commits is not fetched, and its worktree's new branch starts
    /// with no commits, whatever its base names.
    /// </summary>
    /// <remarks>
    /// With <see cref="WorkspaceCreateRequest.SkipFailures"/>, a repository whose fetch or worktree fails is skipped and
    /// reported. Otherwise, and for any cancellation, the first failure unlocks and removes the worktrees already made,
    /// deletes the branches made for them and the folder, and is thrown. A creation that makes no worktree deletes the
    /// folder and returns an empty <see cref="WorkspaceCreateResult.Worktrees"/>.
    /// </remarks>
    public async Task<WorkspaceCreateResult> CreateAsync(WorkspaceCreateRequest request, WorkspaceActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        string folder = FolderOf(request.Folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BranchName);
        RequireNames(request.Repositories);
        if (GitPathChecks.NamedPathExists(folder)) throw new IOException($"The workspace folder {folder} already exists.");

        Log.CreatingFolder(this.logger, folder, actor.Actor, actor.Trigger);
        Directory.CreateDirectory(folder);
        List<WorkspaceWorktree> made = [];
        List<WorkspaceSkippedRepository> skipped = [];
        try
        {
            foreach (WorkspaceRepository repository in request.Repositories)
            {
                string path = Path.Combine(folder, repository.Name);
                try
                {
                    if (await this.git.HasCommitsAsync(repository.SourceRepository, cancellationToken).ConfigureAwait(false))
                        await this.FetchBaseAsync(repository, actor, cancellationToken).ConfigureAwait(false);
                    Log.CreatingWorktree(this.logger, path, request.BranchName, repository.BaseRef, actor.Actor, actor.Trigger);
                    GitWorktreeSnapshot worktree = await this.git.CreateWorktreeAsync(
                        new(repository.SourceRepository, path, repository.BaseRef, request.BranchName), cancellationToken).ConfigureAwait(false);
                    made.Add(new(repository, path, worktree));
                    await this.LockAsync(path, actor, cancellationToken).ConfigureAwait(false);
                }
                // A lock that fails after its worktree was made is not a skip: the creation is undone.
                catch (Exception error) when (request.SkipFailures && !cancellationToken.IsCancellationRequested
                    && made.LastOrDefault()?.Path != path)
                {
                    string reason = WithoutFinalPeriod(error.Message);
                    Log.Skipping(this.logger, repository.Name, reason, actor.Actor, actor.Trigger);
                    skipped.Add(new(repository, reason));
                }
            }
            if (made.Count > 0) Directory.CreateDirectory(Path.Combine(folder, TemporaryFolderName, HomeFolderName));
        }
        catch
        {
            await this.UndoAsync(made, actor).ConfigureAwait(false);
            this.DeleteFolder(folder, actor);
            throw;
        }
        if (made.Count == 0) this.DeleteFolder(folder, actor);
        return new(folder, made.AsReadOnly(), skipped.AsReadOnly());
    }

    /// <summary>
    /// Reports each configured repository's state in the workspace folder; every other top-level folder that is a git
    /// worktree's own folder; and every remaining top-level item except <c>tmp</c> as a stray, so a caller can warn about
    /// it instead of refusing.
    /// </summary>
    /// <remarks>A repository is healthy when its folder is a worktree's own folder with the repository's Git common directory, locked or not.</remarks>
    public async Task<WorkspaceInspection> InspectAsync(string folder, IReadOnlyList<WorkspaceRepository> repositories,
        CancellationToken cancellationToken = default)
    {
        folder = FolderOf(folder);
        RequireNames(repositories);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The workspace folder {folder} does not exist.");
        List<WorkspaceRepositoryStatus> statuses = [];
        foreach (WorkspaceRepository repository in repositories)
        {
            string path = Path.Combine(folder, repository.Name);
            if (!GitPathChecks.NamedPathExists(path))
            {
                statuses.Add(new(repository, path, WorkspaceRepositoryState.Missing, null));
                continue;
            }
            GitRepositorySnapshot? worktree = Directory.Exists(path)
                ? await this.git.InspectAsync(path, cancellationToken).ConfigureAwait(false) : null;
            GitRepositorySnapshot? source = await this.git.InspectAsync(repository.SourceRepository, cancellationToken).ConfigureAwait(false);
            bool healthy = worktree is not null && source is not null && worktree.WorktreePath == this.paths.Resolve(path)
                && worktree.CommonDirectory == source.CommonDirectory;
            statuses.Add(new(repository, path, healthy ? WorkspaceRepositoryState.Healthy : WorkspaceRepositoryState.Broken,
                healthy ? worktree!.BranchRef : null));
        }
        HashSet<string> names = [TemporaryFolderName, .. repositories.Select(repository => repository.Name)];
        List<string> strays = [];
        List<string> others = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder).Where(entry => !names.Contains(Path.GetFileName(entry)))
            .Order(StringComparer.Ordinal))
        {
            bool worktree = Directory.Exists(entry) && await this.OwnWorktreeAsync(entry, cancellationToken).ConfigureAwait(false) is not null;
            (worktree ? others : strays).Add(entry);
        }
        return new(folder, statuses.AsReadOnly(), strays.AsReadOnly(), others.AsReadOnly());
    }

    /// <summary>
    /// Removes a workspace folder, or a single worktree, named by its absolute path: each worktree in it, unlocked first,
    /// then the branch each was on, then the folder with everything left in it, such as <c>tmp</c>.
    /// </summary>
    /// <remarks>
    /// Before anything is removed, every worktree must lose nothing but ignored files, such as build output, and every
    /// commit on its branch must be on a remote-tracking branch; and a workspace folder must hold nothing at its top level
    /// but worktrees and <c>tmp</c>. Otherwise <see cref="WorkspaceRemovalBlockedException"/> names the worktree or the
    /// items, and nothing is removed. A worktree Git keeps is locked again and the removal stops with an
    /// <see cref="IOException"/>.
    /// </remarks>
    public async Task<WorkspaceRemovalResult> RemoveAsync(string path, WorkspaceActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        path = FolderOf(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"The folder {path} does not exist.");
        List<(string Path, GitRepositorySnapshot Worktree)> members = [];
        List<string> strays = [];
        if (await this.OwnWorktreeAsync(path, cancellationToken).ConfigureAwait(false) is { } single) members.Add((path, single));
        else
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(entry) == TemporaryFolderName) continue;
                if (Directory.Exists(entry) && await this.OwnWorktreeAsync(entry, cancellationToken).ConfigureAwait(false) is { } member)
                    members.Add((entry, member));
                else strays.Add(entry);
            }
        }
        if (strays.Count > 0)
            throw new WorkspaceRemovalBlockedException($"The workspace {path} holds items that are not git worktrees: "
                + $"{string.Join(", ", strays)}. Repair the workspace, or move them, first. Nothing was removed.");
        foreach ((string member, _) in members)
        {
            GitPreservationCheck check = await this.git.CheckPreservationAsync(member, cancellationToken).ConfigureAwait(false);
            if (!GitPreservationCheck.LosesOnlyIgnoredFiles(check))
                throw new WorkspaceRemovalBlockedException($"The worktree {member} cannot be removed without loss: "
                    + $"{string.Join(" ", check.Blockers.Where(blocker => blocker != GitPreservationCheck.IgnoredFilesBlocker))} Nothing was removed.");
            // A branch with no commits has nothing to publish.
            if (check.HeadCommit is not { } head) continue;
            IReadOnlyList<string> unpublished = await this.git.ReadUnpublishedCommitsAsync(member, head, cancellationToken)
                .ConfigureAwait(false);
            if (unpublished.Count > 0)
                throw new WorkspaceRemovalBlockedException($"The worktree {member} has {unpublished.Count} commit(s) on no remote branch. "
                    + "Push them first. Nothing was removed.");
        }

        List<GitWorktreeSnapshot> branches = [];
        foreach ((string member, GitRepositorySnapshot worktree) in members)
        {
            Log.Unlocking(this.logger, member, actor.Actor, actor.Trigger);
            await this.git.UnlockWorktreeAsync(member, cancellationToken).ConfigureAwait(false);
            Log.RemovingWorktree(this.logger, member, actor.Actor, actor.Trigger);
            GitRemovalResult removed = await this.git.RemoveWorktreeDiscardingIgnoredAsync(member, cancellationToken).ConfigureAwait(false);
            if (removed.Status != GitRemovalStatus.Removed)
            {
                // A worktree Git keeps stays a workspace worktree, so it gets its lock back.
                await this.LockAsync(member, actor, CancellationToken.None).ConfigureAwait(false);
                throw new IOException($"Git kept the worktree {member}: "
                    + string.Join(" ", [.. removed.Preservation.Blockers.Where(blocker => blocker != GitPreservationCheck.IgnoredFilesBlocker),
                        .. removed.Diagnostic is { } diagnostic ? [diagnostic] : Array.Empty<string>()])
                    + " Nothing further was deleted.");
            }
            if (removed.BranchRef is { } branch && branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                && removed.Preservation.HeadCommit is { } head)
                branches.Add(new(worktree.CommonDirectory, worktree.WorktreePath, branch, head));
        }
        // Each branch is deleted only while it still names the commit its worktree had at removal.
        foreach (GitWorktreeSnapshot branch in branches)
        {
            Log.DeletingBranch(this.logger, branch.BranchRef, branch.WorktreePath, actor.Actor, actor.Trigger);
            await this.git.DeleteCreatedBranchAsync(branch, CancellationToken.None).ConfigureAwait(false);
        }
        if (Directory.Exists(path))
        {
            Log.DeletingFolder(this.logger, path, actor.Actor, actor.Trigger);
            Directory.Delete(path, recursive: true);
        }
        return new(path, [.. members.Select(member => member.Path)], [.. branches.Select(branch => branch.BranchRef)]);
    }

    // The worktree whose own folder the path is, or null.
    private async Task<GitRepositorySnapshot?> OwnWorktreeAsync(string path, CancellationToken cancellationToken) =>
        await this.git.InspectAsync(path, cancellationToken).ConfigureAwait(false) is { } worktree
            && worktree.WorktreePath == this.paths.Resolve(path) ? worktree : null;

    // Fetches the base's branch first when the base names a remote-tracking branch, <remote>/<branch>.
    private async Task FetchBaseAsync(WorkspaceRepository repository, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> remotes = await this.git.ReadRemoteNamesAsync(repository.SourceRepository, cancellationToken).ConfigureAwait(false);
        string start = repository.BaseRef;
        if (remotes.FirstOrDefault(remote => start.Length > remote.Length + 1 && start.StartsWith(remote + "/", StringComparison.Ordinal))
            is not { } named) return;
        string branch = start[(named.Length + 1)..];
        Log.Fetching(this.logger, start, repository.SourceRepository, actor.Actor, actor.Trigger);
        GitFetchResult fetched = await this.git.FetchAsync(new(repository.SourceRepository, named,
            [$"refs/heads/{branch}:refs/remotes/{named}/{branch}"]), cancellationToken).ConfigureAwait(false);
        if (fetched.Diagnostic is { } diagnostic) throw new IOException($"Could not fetch {start} in {repository.SourceRepository}: {diagnostic}");
    }

    private async Task LockAsync(string worktree, WorkspaceActor actor, CancellationToken cancellationToken)
    {
        Log.Locking(this.logger, worktree, actor.Actor, actor.Trigger);
        await this.git.LockWorktreeAsync(worktree, LockReason, cancellationToken).ConfigureAwait(false);
    }

    // Unlocks and removes the worktrees a failed creation made, newest first, then the branches it made for them.
    private async Task UndoAsync(IReadOnlyList<WorkspaceWorktree> made, WorkspaceActor actor)
    {
        foreach (WorkspaceWorktree worktree in made.Reverse())
        {
            try
            {
                Log.Unlocking(this.logger, worktree.Path, actor.Actor, actor.Trigger);
                await this.git.UnlockWorktreeAsync(worktree.Path, CancellationToken.None).ConfigureAwait(false);
                Log.RemovingWorktree(this.logger, worktree.Path, actor.Actor, actor.Trigger);
                GitRemovalResult removed = await this.git.RemoveWorktreeAsync(worktree.Path, CancellationToken.None).ConfigureAwait(false);
                if (removed.Status != GitRemovalStatus.Removed)
                    Log.UndoKept(this.logger, worktree.Path, worktree.Worktree.BranchRef, removed.Status.ToString(), removed.Diagnostic);
                // A branch made with no commits has no ref to delete.
                else if (worktree.Worktree.BaseCommit is not null)
                {
                    Log.DeletingBranch(this.logger, worktree.Worktree.BranchRef, worktree.Path, actor.Actor, actor.Trigger);
                    await this.git.DeleteCreatedBranchAsync(worktree.Worktree, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Log.UndoFailed(this.logger, error, worktree.Path, worktree.Worktree.BranchRef);
            }
        }
    }

    private void DeleteFolder(string folder, WorkspaceActor actor)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            Log.DeletingFolder(this.logger, folder, actor.Actor, actor.Trigger);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.FolderDeleteFailed(this.logger, error, folder);
        }
    }

    private static string FolderOf(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Give the workspace folder's absolute path.", nameof(folder));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    private static void RequireNames(IReadOnlyList<WorkspaceRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        HashSet<string> seen = [];
        foreach (WorkspaceRepository repository in repositories)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentException.ThrowIfNullOrWhiteSpace(repository.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(repository.SourceRepository);
            ArgumentException.ThrowIfNullOrWhiteSpace(repository.BaseRef);
            if (repository.Name != Path.GetFileName(repository.Name) || repository.Name is "." or ".." or TemporaryFolderName
                || !seen.Add(repository.Name))
                throw new ArgumentException($"The repository name \"{repository.Name}\" is not a distinct folder name.", nameof(repositories));
        }
    }

    // A message as a clause inside a sentence: without its final period.
    private static string WithoutFinalPeriod(string message) => message.EndsWith('.') ? message[..^1] : message;

    private string Timestamp() => this.time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
