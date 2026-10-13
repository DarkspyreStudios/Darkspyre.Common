using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>
/// Branch create and delete, stash save, list, apply and drop, and path restore, each under the repository's operation
/// gate and on the worktree root. Branch create checks HEAD and branch delete checks the branch's commit. Every refusal
/// throws <see cref="GitOperationException"/> before Git changes anything.
/// </summary>
internal sealed class GitLocalChanges(GitCommands git, GitRepositoryReader reader, GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    // A restore names at most 64 paths, the batch limit every tool shares.
    private const int PathLimit = 64;
    private const string BranchPrefix = "refs/heads/";
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    /// <summary>Runs <c>git branch --no-track</c> at the resolved start point, refusing an existing name.</summary>
    internal async Task<GitBranchResult> CreateBranchAsync(GitBranchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireBranchName(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartPoint);
        RequireOptionalCommit(request.ExpectedHead);
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        RequireHead(gate.State, request.ExpectedHead);
        string branchRef = await this.CheckedBranchRefAsync(gate.State.Worktree, request.Name, cancellationToken).ConfigureAwait(false);
        if (await this.RefCommitAsync(gate.State.Worktree, branchRef, cancellationToken).ConfigureAwait(false) is not null)
            throw new GitOperationException(GitFailure.BranchExists, $"Branch '{request.Name}' already exists; nothing changed.");
        string commit = await this.reader.ResolveCommitAsync(gate.State.Worktree, request.StartPoint, cancellationToken)
            .ConfigureAwait(false);
        RequireCompleted(await this.git.RunAsync(gate.State.Worktree, ["branch", "--no-track", request.Name, commit], cancellationToken)
            .ConfigureAwait(false), "create the branch");
        return new(branchRef, commit);
    }

    /// <summary>Runs <c>git branch -D</c> while the branch names the expected commit and no worktree has it checked out.</summary>
    internal async Task<GitBranchResult> DeleteBranchAsync(GitBranchDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireBranchName(request.Name);
        if (!IsCommitId(request.ExpectedCommit)) throw new ArgumentException("A branch delete needs the expected commit ID.", nameof(request));
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string branchRef = await this.CheckedBranchRefAsync(gate.State.Worktree, request.Name, cancellationToken).ConfigureAwait(false);
        string commit = await this.RefCommitAsync(gate.State.Worktree, branchRef, cancellationToken).ConfigureAwait(false)
            ?? throw new GitOperationException(GitFailure.RevisionNotFound, $"Branch '{request.Name}' does not exist; nothing was deleted.");
        if (!string.Equals(commit, request.ExpectedCommit, StringComparison.OrdinalIgnoreCase))
            throw new GitOperationException(GitFailure.StaleState, $"Branch '{request.Name}' does not name the expected commit; nothing was deleted.");
        if (await this.CheckedOutInAsync(gate.State.Worktree, branchRef, cancellationToken).ConfigureAwait(false) is { } worktree)
            throw new GitOperationException(GitFailure.BranchCheckedOut,
                $"Branch '{request.Name}' is checked out in '{worktree}'; nothing was deleted.");
        RequireCompleted(await this.git.RunAsync(gate.State.Worktree, ["branch", "-D", request.Name], cancellationToken)
            .ConfigureAwait(false), "delete the branch");
        return new(branchRef, commit);
    }

    /// <summary>
    /// Runs <c>git stash push -m</c>, with <c>--include-untracked</c> when asked. The stash ref moving decides whether
    /// anything was stashed.
    /// </summary>
    internal async Task<GitStashSaveResult> SaveStashAsync(GitStashSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        if (request.Message.Contains('\0')) throw new ArgumentException("A stash message has no null characters.", nameof(request));
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string? before = await this.RefCommitAsync(gate.State.Worktree, "refs/stash", cancellationToken).ConfigureAwait(false);
        RequireCompleted(await this.git.RunAsync(gate.State.Worktree,
            request.IncludeUntracked ? ["stash", "push", "--include-untracked", "-m", request.Message] : ["stash", "push", "-m", request.Message],
            cancellationToken).ConfigureAwait(false), "save the stash");
        string? after = await this.RefCommitAsync(gate.State.Worktree, "refs/stash", cancellationToken).ConfigureAwait(false);
        return after is null || after == before ? new(null, null) : new("stash@{0}", after);
    }

    /// <summary>Reads <c>git stash list</c>: each stash's commit and reflog subject, newest first.</summary>
    internal async Task<IReadOnlyList<GitStashEntry>> ListStashesAsync(string repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        GitRepositoryReader.GitLayout layout = await this.LocateRootAsync(repository, cancellationToken).ConfigureAwait(false);
        return await this.ReadStashesAsync(layout.WorktreePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>git stash apply</c> on the stash's commit. Unmerged paths afterwards are Conflicted; the stash stays either way.
    /// </summary>
    internal async Task<GitStashApplyResult> ApplyStashAsync(GitStashApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCommitId(request.StashCommit)) throw new ArgumentException("A stash apply needs the stash's commit ID.", nameof(request));
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        GitStashEntry stash = await this.FindStashAsync(gate.State.Worktree, request.StashCommit, cancellationToken).ConfigureAwait(false);
        GitRunResult applied = await this.git.RunAsync(gate.State.Worktree, ["stash", "apply", stash.StashCommit], cancellationToken)
            .ConfigureAwait(false);
        GitWorkingState after = await this.reader.ReadWorkingStateAsync(gate.State.Worktree, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The worktree is unavailable after the stash apply.");
        if (after.ConflictedPaths.Count != 0)
            return new(GitSequenceState.Conflicted,
                await this.reader.PermittedAsync(after.Worktree, after.ConflictedPaths, cancellationToken).ConfigureAwait(false));
        RequireCompleted(applied, "apply the stash");
        return new(GitSequenceState.Completed, []);
    }

    /// <summary>Runs <c>git stash drop stash@{n}</c> for the entry whose commit matches.</summary>
    internal async Task<GitStashDropResult> DropStashAsync(GitStashDropRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCommitId(request.StashCommit)) throw new ArgumentException("A stash drop needs the stash's commit ID.", nameof(request));
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        GitStashEntry stash = await this.FindStashAsync(gate.State.Worktree, request.StashCommit, cancellationToken).ConfigureAwait(false);
        RequireCompleted(await this.git.RunAsync(gate.State.Worktree, ["stash", "drop", $"stash@{{{stash.Index}}}"], cancellationToken)
            .ConfigureAwait(false), "drop the stash");
        return new(true);
    }

    /// <summary>
    /// Checks each path with <c>git cat-file -e commit:path</c>, then runs <c>git restore --source --worktree</c> on the
    /// present ones only, because Git refuses the whole command when one pathspec matches nothing. The index is untouched.
    /// </summary>
    internal async Task<GitRestoreResult> RestorePathsAsync(GitRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision);
        if (request.Paths.Count is 0 or > PathLimit || request.Paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Contains('\0')))
            throw new ArgumentException("A restore names 1 to 64 paths without null characters.", nameof(request));
        await using Gate gate = await this.EnterAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string commit = await this.reader.ResolveCommitAsync(gate.State.Worktree, request.Revision, cancellationToken)
            .ConfigureAwait(false);
        string[] paths = [.. request.Paths.Select(path => RelativePath(gate.State.Worktree, path)).Distinct(StringComparer.Ordinal)];
        List<string> present = [];
        List<string> missing = [];
        foreach (string path in paths)
        {
            GitRunResult found = await this.git.RunReadAsync(gate.State.Worktree, ["cat-file", "-e", commit + ":" + path],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (found.Exit.Diagnostic is not null) Check(found, "check a path at the revision");
            (found.Exit.ExitCode == 0 ? present : missing).Add(path);
        }
        if (present.Count != 0)
        {
                RequireCompleted(await this.git.RunAsync(gate.State.Worktree,
                ["--literal-pathspecs", "restore", "--source=" + commit, "--worktree", "--", .. present], cancellationToken).ConfigureAwait(false),
                "restore the paths");
        }
        return new(commit, present.AsReadOnly(), missing.AsReadOnly());
    }

    // The worktree root as the path policy resolves it, before the gate.
    private async Task<GitRepositoryReader.GitLayout> LocateRootAsync(string repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        GitRepositoryReader.GitLayout layout = await this.reader.LocateAsync(repository, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The worktree is unavailable.");
        if (this.paths.Resolve(repository) != layout.WorktreePath)
            throw new ArgumentException("This Git operation requires the worktree root, not a subdirectory.", nameof(repository));
        return layout;
    }

    // Locates the worktree root, checks once that the path policy permits writing the repository and the worktree, enters the
    // repository's operation gate and reads the working state under it.
    private async Task<Gate> EnterAsync(string repository, CancellationToken cancellationToken)
    {
        GitRepositoryReader.GitLayout layout = await this.LocateRootAsync(repository, cancellationToken).ConfigureAwait(false);
        await this.paths.RequireWriteAsync([layout.CommonDirectory, layout.WorktreePath], cancellationToken).ConfigureAwait(false);
        IDisposable entered = await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            GitWorkingState state = await this.reader.ReadWorkingStateAsync(layout.WorktreePath, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The worktree changed while waiting for its operation gate.");
            return new(entered, state);
        }
        catch
        {
            entered.Dispose();
            throw;
        }
    }

    private static void RequireHead(GitWorkingState state, string? expectedHead)
    {
        if (!string.Equals(state.Head, expectedHead, StringComparison.OrdinalIgnoreCase))
            throw new GitOperationException(GitFailure.StaleState, "HEAD does not match the expected commit; nothing changed.");
    }

    // The branch's full ref, after Git confirms the name is a valid branch name.
    private async Task<string> CheckedBranchRefAsync(string worktree, string name, CancellationToken cancellationToken)
    {
        string branchRef = BranchPrefix + name;
        GitRunResult valid = await this.git.RunReadAsync(worktree, ["check-ref-format", branchRef],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (valid.Exit.Diagnostic is not null) Check(valid, "check the branch name");
        if (valid.Exit.ExitCode != 0) throw new ArgumentException($"'{name}' is not a valid branch name.", nameof(name));
        return branchRef;
    }

    // The commit a local ref names, or null when the ref does not exist.
    private async Task<string?> RefCommitAsync(string worktree, string reference, CancellationToken cancellationToken)
    {
        GitRunResult result = await this.git.RunReadAsync(worktree, ["rev-parse", "-q", "--verify", reference + "^{commit}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Exit.ExitCode == 1 && result.Exit.Diagnostic is null) return null;
        Check(result, "read a ref");
        string commit = Encoding.UTF8.GetString(result.Output).TrimEnd('\n');
        return IsCommitId(commit) ? commit : throw new InvalidDataException("Git returned an invalid commit identity.");
    }

    // The worktree whose checked-out branch is the ref, from worktree list --porcelain, or null.
    private async Task<string?> CheckedOutInAsync(string worktree, string branchRef, CancellationToken cancellationToken)
    {
        byte[] output = await this.reader.ReadAsync(worktree, ["worktree", "list", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);
        return GitWorktreeList.Parse(output).FirstOrDefault(entry => entry.BranchRef == branchRef)?.Path;
    }

    private async Task<GitStashEntry> FindStashAsync(string worktree, string stashCommit, CancellationToken cancellationToken) =>
        (await this.ReadStashesAsync(worktree, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(entry => string.Equals(entry.StashCommit, stashCommit, StringComparison.OrdinalIgnoreCase))
            ?? throw new GitOperationException(GitFailure.StashNotFound, $"No stash has commit {stashCommit}; nothing changed.");

    // stash list -z prints a commit and its reflog subject, "On <branch>: <message>" or "WIP on <branch>: <message>", per
    // stash. A branch name never holds a colon, so the first ": " ends it.
    private async Task<IReadOnlyList<GitStashEntry>> ReadStashesAsync(string worktree, CancellationToken cancellationToken)
    {
        byte[] output = await this.reader.ReadAsync(worktree, ["stash", "list", "-z", "--format=%H%x00%gs"], cancellationToken)
            .ConfigureAwait(false);
        string[] fields = Encoding.UTF8.GetString(output).Split('\0');
        if (fields[^1].Length != 0 || fields.Length % 2 != 1) throw new InvalidDataException("Git returned an invalid stash list.");
        List<GitStashEntry> stashes = [];
        for (int index = 0; index + 1 < fields.Length; index += 2)
        {
            if (!IsCommitId(fields[index])) throw new InvalidDataException("Git returned an invalid stash commit.");
            string subject = fields[index + 1];
            string described = subject.StartsWith("WIP on ", StringComparison.Ordinal) ? subject["WIP on ".Length..]
                : subject.StartsWith("On ", StringComparison.Ordinal) ? subject["On ".Length..] : subject;
            int colon = described.IndexOf(": ", StringComparison.Ordinal);
            string? branch = colon < 0 ? null : described[..colon];
            stashes.Add(new(index / 2, fields[index], colon < 0 ? subject : described[(colon + 2)..],
                branch is null or "(no branch)" ? null : branch));
        }
        return stashes.AsReadOnly();
    }

    // A worktree path as a repository-relative Git path, lexically, so a tracked link restores as the link. A folder is
    // allowed; the whole tree and Git metadata are not.
    private string RelativePath(string worktree, string path)
    {
        string absolute = Path.GetFullPath(path, worktree);
        string parent = this.paths.Resolve(Path.GetDirectoryName(absolute) ?? absolute);
        string relative = Path.GetRelativePath(worktree, Path.Combine(parent, Path.GetFileName(absolute)));
        if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith("../", StringComparison.Ordinal)
            || relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
            throw new ArgumentException("Restore paths name worktree content, not the whole tree, a path outside it or Git metadata.", nameof(path));
        return relative;
    }

    private static void RequireBranchName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.StartsWith("refs/", StringComparison.Ordinal) || name.Contains('\0'))
            throw new ArgumentException("A branch is named by its short name, not an option or full ref.", nameof(name));
    }

    private static void RequireOptionalCommit(string? commit)
    {
        if (commit is not null && !IsCommitId(commit)) throw new ArgumentException("The expected HEAD is a commit ID or null.", nameof(commit));
    }

    private static bool IsCommitId(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>One call inside the repository's operation gate and the working state read under it.</summary>
    private sealed class Gate(IDisposable entered, GitWorkingState state) : IAsyncDisposable
    {
        internal GitWorkingState State { get; } = state;

        public ValueTask DisposeAsync()
        {
            entered.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
