using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>
/// Explicit-path staging and unstaging, and ordinary commits, under the repository's operation gate. Git runs the
/// repository's own hooks.
/// </summary>
internal sealed class GitCommits(GitCommands git, GitRepositoryReader reader, GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    /// <summary>
    /// Stages exactly the requested paths. Entries staged earlier stay staged. The result names the requested paths whose
    /// index entries now differ from HEAD.
    /// </summary>
    internal async Task<GitStageResult> StageAsync(GitStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string[] supplied = RequirePaths(request.Paths);
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.Repository, "stage",
            "Staging requires the worktree root, not a subdirectory.", cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            string[] paths = supplied.Select(path => RelativePath(worktree, path)).Distinct(StringComparer.Ordinal).ToArray();
            // Stage only the named paths; a deleted tracked path is staged as its deletion. Nothing is reset on failure.
            RequireCompleted(await this.git.RunAsync(worktree, ["--literal-pathspecs", "add", "--", .. paths],
                cancellationToken).ConfigureAwait(false), "stage the requested paths");
            byte[] listed = await this.reader.ReadAsync(worktree, ["diff", "--cached", "--name-only", "-z", "--", .. paths],
                cancellationToken).ConfigureAwait(false);
            HashSet<string> changed = [.. Encoding.UTF8.GetString(listed).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            return new(Array.AsReadOnly(paths.Where(changed.Contains).ToArray()));
        }
    }

    /// <summary>
    /// Removes exactly the requested paths from the index and keeps their working files: <c>restore --staged
    /// --source=HEAD</c> when HEAD names a commit, otherwise <c>update-index --force-remove</c>, which drops only those
    /// index entries and never refuses a path whose staged and working content differ.
    /// </summary>
    internal async Task<GitUnstageResult> UnstageAsync(GitUnstageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string[] supplied = RequirePaths(request.Paths);
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.Repository, "unstage",
            "Unstaging requires the worktree root, not a subdirectory.", cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            string[] paths = supplied.Select(path => RelativePath(worktree, path)).Distinct(StringComparer.Ordinal).ToArray();
            string[] arguments = layout.HeadCommit is null
                ? ["update-index", "--force-remove", "--", .. paths]
                : ["--literal-pathspecs", "restore", "--staged", "--source=HEAD", "--", .. paths];
            RequireCompleted(await this.git.RunAsync(worktree, arguments, cancellationToken).ConfigureAwait(false),
                "unstage the requested paths");
            return new(Array.AsReadOnly(paths));
        }
    }

    /// <summary>
    /// Commits the index as it is with <c>commit --no-edit -m</c> on the checked-out branch. It runs no <c>add</c> and no
    /// <c>--only</c>, so a working edit made after staging stays pending. Git runs the repository's hooks in the commit's
    /// own child.
    /// </summary>
    internal async Task<GitCommitResult> CommitStagedAsync(GitCommitStagedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireMessage(request.Message);
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.Repository, "commit",
            "Commit requires the worktree root, not a subdirectory.", cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            string branchRef = await this.CheckedOutBranchAsync(worktree, cancellationToken).ConfigureAwait(false);
            RequireCompleted(await this.git.RunAsync(worktree, ["commit", "--no-edit", "-m", request.Message], cancellationToken)
                .ConfigureAwait(false), "commit the staged changes (inspect current state after failure; no reset or retry was attempted)");
            string commitId = await this.reader.ResolveCommitAsync(worktree, "HEAD", cancellationToken).ConfigureAwait(false);
            return new(commitId, branchRef);
        }
    }

    /// <summary>
    /// Stages and commits exactly the requested paths with <c>commit --only</c> on the checked-out branch. Git runs the
    /// repository's hooks in the commit's own child. The result's commit is HEAD as Git left it, read under the gate.
    /// </summary>
    internal async Task<GitCommitResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireMessage(request.Message);
        string[] supplied = RequirePaths(request.Paths);
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.WorktreePath, "commit",
            "Commit requires the worktree root, not a subdirectory.", cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            string branchRef = await this.CheckedOutBranchAsync(worktree, cancellationToken).ConfigureAwait(false);
            string[] paths = supplied.Select(path => RelativePath(worktree, path)).Distinct(StringComparer.Ordinal).ToArray();
            // New files must be known to Git for --only, so the named paths present in the worktree are added; never
            // stage-all or reset on failure. A path absent from the worktree goes to --only alone, which commits its
            // deletion; git add refuses one whose deletion is already staged. Git's --only index excludes unrelated
            // pre-staged data and retains it.
            string[] present = paths.Where(path => InWorktree(worktree, path)).ToArray();
            if (present.Length > 0)
                RequireCompleted(await this.git.RunAsync(worktree, ["--literal-pathspecs", "add", "--", .. present],
                    cancellationToken).ConfigureAwait(false), "stage requested commit paths");
            // Git runs the repository's configured hooks in this child, under the caller's sandbox.
            RequireCompleted(await this.git.RunAsync(worktree,
                ["--literal-pathspecs", "commit", "--only", "--no-edit", "-m", request.Message, "--", .. paths],
                cancellationToken).ConfigureAwait(false),
                "commit requested paths (inspect current state after failure; no reset or retry was attempted)");
            // The gate keeps every other host mutation out, so HEAD is the commit Git made unless a user hook moved it.
            string commitId = await this.reader.ResolveCommitAsync(worktree, "HEAD", cancellationToken).ConfigureAwait(false);
            return new(commitId, branchRef);
        }
    }

    // Locate the worktree root, check write authority once, then wait for the repository's operation gate.
    private async Task<(GitRepositoryReader.GitLayout Layout, IDisposable Operation)> EnterAsync(string repository,
        string operation, string notRoot, CancellationToken cancellationToken)
    {
        GitRepositoryReader.GitLayout layout = await this.reader.LocateAsync(repository, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException($"The {operation} worktree is unavailable.");
        if (this.paths.Resolve(repository) != layout.WorktreePath) throw new ArgumentException(notRoot, nameof(repository));
        await this.paths.RequireWriteAsync([layout.CommonDirectory, layout.WorktreePath], cancellationToken).ConfigureAwait(false);
        return (layout, await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false));
    }

    // The checked-out branch's full ref; a detached HEAD refuses the commit before anything is staged.
    private async Task<string> CheckedOutBranchAsync(string worktree, CancellationToken cancellationToken)
    {
        GitRunResult head = await this.git.RunReadAsync(worktree, ["symbolic-ref", "--quiet", "HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (head.Exit is { ExitCode: 1, Diagnostic: null })
            throw new GitOperationException(GitFailure.StaleState, "HEAD is detached; a commit needs a checked-out branch. Nothing was staged.");
        Check(head, "read the checked-out branch");
        return Encoding.UTF8.GetString(head.Output).TrimEnd('\n');
    }

    private static void RequireMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Contains('\0')) throw new ArgumentException("A commit message cannot contain a null character.", nameof(message));
    }

    // A symbolic link is an entry even when its target is missing.
    private static bool InWorktree(string worktree, string relative)
    {
        FileInfo entry = new(Path.Combine(worktree, relative));
        return entry.Exists || entry.LinkTarget is not null;
    }

    private static string[] RequirePaths(IReadOnlyList<string>? paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] supplied = [.. paths];
        if (supplied.Length == 0 || supplied.Any(path => string.IsNullOrWhiteSpace(path) || path.Contains('\0')))
            throw new ArgumentException("Explicit nonempty paths without null characters are required.", nameof(paths));
        return supplied;
    }

    private string RelativePath(string worktree, string path)
    {
        // This is a Git pathspec, not a request to dereference its file: tracked symlinks must be
        // committed as symlinks. Normalize lexically, then let literal-pathspec Git handle the entry.
        string absolute = Path.GetFullPath(path, worktree);
        string parent = this.paths.Resolve(Path.GetDirectoryName(absolute) ?? absolute);
        string relative = Path.GetRelativePath(worktree, Path.Combine(parent, Path.GetFileName(absolute)));
        if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith("../", StringComparison.Ordinal)
            || relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
            throw new ArgumentException("Commit paths must name explicit worktree content, not the whole tree or Git metadata.", nameof(path));
        // A directory would stage everything below it; a symbolic link to one is an entry and stays allowed.
        DirectoryInfo entry = new(Path.Combine(worktree, relative));
        if (entry.Exists && entry.LinkTarget is null)
            throw new ArgumentException("Paths must name files or entries, not a directory.", nameof(path));
        return relative;
    }
}
