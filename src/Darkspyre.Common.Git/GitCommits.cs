using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>
/// Explicit-path staging and ordinary commits under the repository's operation gate. Git runs the repository's own hooks.
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
    /// Stages and commits exactly the requested paths with <c>commit --only</c> on the checked-out branch. Git runs the
    /// repository's hooks in the commit's own child. The result's commit is HEAD as Git left it, read under the gate.
    /// </summary>
    internal async Task<GitCommitResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        string[] supplied = RequirePaths(request.Paths);
        if (request.Message.Contains('\0')) throw new ArgumentException("A commit message cannot contain a null character.", nameof(request));
        (GitRepositoryReader.GitLayout layout, IDisposable operation) = await this.EnterAsync(request.WorktreePath, "commit",
            "Commit requires the worktree root, not a subdirectory.", cancellationToken).ConfigureAwait(false);
        using (operation)
        {
            string worktree = layout.WorktreePath;
            GitRunResult head = await this.git.RunReadAsync(worktree, ["symbolic-ref", "--quiet", "HEAD"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (head.Exit is { ExitCode: 1, Diagnostic: null })
                throw new GitOperationException(GitFailure.StaleState, "HEAD is detached; a commit needs a checked-out branch. Nothing was staged.");
            Check(head, "read the checked-out branch");
            string branchRef = Encoding.UTF8.GetString(head.Output).TrimEnd('\n');
            string[] paths = supplied.Select(path => RelativePath(worktree, path)).Distinct(StringComparer.Ordinal).ToArray();
            // New files must be known to Git for --only. Stage only named paths; never stage-all or
            // reset on failure. Git's --only index excludes unrelated pre-staged data and retains it.
            RequireCompleted(await this.git.RunAsync(worktree, ["--literal-pathspecs", "add", "--", .. paths],
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
