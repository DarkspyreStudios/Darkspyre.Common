using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>Explicit linked-worktree reset under the shared repository gate.</summary>
internal sealed class GitWorkspaceTransitions(GitCommands git, GitRepositoryReader reader,
    GitRepositoryLocks locks,
    IGitPathPolicy paths)
{
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    internal async Task<GitResetResult> ResetAsync(GitResetRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        GitRepositoryReader.GitLayout? layout = await this.LocateAsync(request.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (layout is null || !string.Equals(this.paths.Resolve(request.WorktreePath), layout.WorktreePath, StringComparison.Ordinal))
            return await this.RefusedAsync(request.WorktreePath, "The path is not a preserved linked worktree or HEAD changed.",
                cancellationToken).ConfigureAwait(false);
        if (!await this.CanWriteAsync([layout.CommonDirectory, layout.WorktreePath], cancellationToken).ConfigureAwait(false))
            return await this.RefusedAsync(layout.WorktreePath, "The scope cannot write the repository and worktree.", cancellationToken)
                .ConfigureAwait(false);
        using IDisposable gate = await this.locks.EnterAsync(layout.CommonDirectory, cancellationToken).ConfigureAwait(false);
        // Inspection and preservation stay under the gate: they are the data-loss check the reset depends on.
        GitRepositorySnapshot current = await this.RequireExactWorktreeAsync(layout.WorktreePath, cancellationToken).ConfigureAwait(false);
        GitPreservationCheck preservation = await this.reader.CheckPreservationAsync(current.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (!preservation.CanResetWithoutLoss || !SameHead(current.HeadCommit, request.ExpectedHeadCommit) || current.HeadCommit != preservation.HeadCommit)
            return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [], "The path is not a preserved linked worktree or HEAD changed.");
        string branch;
        string[] command;
        switch (request.Target)
        {
            case GitResetToNewBranch fresh:
                branch = fresh.BranchName;
                if (!await this.ValidBranchAsync(current.WorktreePath, branch, cancellationToken).ConfigureAwait(false))
                    return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [], "The new branch name is invalid.");
                if (await this.RefExistsAsync(current.WorktreePath, branch, cancellationToken).ConfigureAwait(false))
                    return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [], "The new branch already exists.");
                string start;
                try { start = await this.reader.ResolveCommitAsync(current.WorktreePath, fresh.StartRef, cancellationToken).ConfigureAwait(false); }
                catch (IOException error)
                {
                    return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [],
                        $"The requested start ref is unavailable: {error.Message}");
                }
                command = ["switch", "--no-track", "-c", branch, start];
                break;
            case GitResetToExistingBranch existing:
                branch = existing.BranchName;
                if (!await this.ValidBranchAsync(current.WorktreePath, branch, cancellationToken).ConfigureAwait(false)
                    || !await this.RefExistsAsync(current.WorktreePath, branch, cancellationToken).ConfigureAwait(false))
                    return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [], "The target branch is unavailable.");
                if (await this.BranchOccupiedAsync(current.WorktreePath, current.WorktreePath, branch, cancellationToken).ConfigureAwait(false))
                    return new(GitResetStatus.Refused, preservation, current.HeadCommit, current.BranchRef, [], "The target branch is checked out in another worktree.");
                command = ["switch", branch];
                break;
            default: throw new ArgumentException("Unknown reset target.", nameof(request));
        }
        GitRunResult switched = await this.git.RunAsync(current.WorktreePath, command, cancellationToken).ConfigureAwait(false);
        GitRepositorySnapshot afterSwitch = await this.RequireExactWorktreeAsync(current.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (switched.Exit.ExitCode != 0 || switched.Exit.Diagnostic is not null
            || afterSwitch.BranchRef != "refs/heads/" + branch || afterSwitch.CommonDirectory != current.CommonDirectory)
            return new(GitResetStatus.Failed, preservation, afterSwitch.HeadCommit, afterSwitch.BranchRef, [],
                "Git did not confirm the requested branch switch; inspect the worktree before another action.");
        string[] untracked = afterSwitch.Status.Where(entry => entry.IsUntracked).Select(entry => entry.Path).ToArray();
        GitRunResult cleaned = await this.git.RunAsync(afterSwitch.WorktreePath, ["clean", "-d", "-f"], cancellationToken).ConfigureAwait(false);
        GitRepositorySnapshot finished = await this.RequireExactWorktreeAsync(current.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (cleaned.Exit.ExitCode != 0 || cleaned.Exit.Diagnostic is not null || finished.BranchRef != afterSwitch.BranchRef
            || finished.HeadCommit != afterSwitch.HeadCommit || finished.Status.Any(entry => !entry.IsIgnored))
            return new(GitResetStatus.Failed, preservation, finished.HeadCommit, finished.BranchRef, [],
                "Git could not confirm a clean switched worktree; inspect before another action.");
        return new(GitResetStatus.Reset, preservation, finished.HeadCommit, finished.BranchRef,
            Array.AsReadOnly(untracked));
    }

    // A path that is not a readable working tree is located as none, and the refusal reports its preservation.
    private async Task<GitRepositoryReader.GitLayout?> LocateAsync(string path, CancellationToken token)
    {
        try { return await this.reader.LocateAsync(path, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or NotSupportedException) { return null; }
    }

    private async Task<GitResetResult> RefusedAsync(string path, string reason, CancellationToken token)
    {
        GitPreservationCheck preservation = await this.reader.CheckPreservationAsync(path, token).ConfigureAwait(false);
        return new(GitResetStatus.Refused, preservation, preservation.HeadCommit, preservation.BranchRef, [], reason);
    }

    private async Task<GitRepositorySnapshot> RequireExactWorktreeAsync(string path, CancellationToken token)
    {
        GitRepositorySnapshot result = await this.reader.InspectAsync(path, token).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The Git worktree is unavailable.");
        if (!string.Equals(this.paths.Resolve(path), result.WorktreePath, StringComparison.Ordinal))
            throw new ArgumentException("The operation requires the exact worktree root.", nameof(path));
        return result;
    }

    private async Task<bool> CanWriteAsync(IReadOnlyList<string> written, CancellationToken cancellationToken)
    {
        try
        {
            await this.paths.RequireWriteAsync(written, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<bool> ValidBranchAsync(string path, string branch, CancellationToken token)
    {
        GitRunResult check = await this.git.RunAsync(path, ["check-ref-format", "--branch", branch], token).ConfigureAwait(false);
        return check.Exit.ExitCode == 0 && check.Exit.Diagnostic is null;
    }

    private async Task<bool> RefExistsAsync(string path, string branch, CancellationToken token)
    {
        GitRunResult result = await this.git.RunAsync(path,
            ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], token).ConfigureAwait(false);
        if (result.Exit.ExitCode is not (0 or 1) || result.Exit.Diagnostic is not null) Check(result, "inspect branch");
        return result.Exit.ExitCode == 0;
    }

    private async Task<bool> BranchOccupiedAsync(string path, string current,
        string branch, CancellationToken token)
    {
        byte[] output = await this.reader.ReadAsync(path, ["worktree", "list", "--porcelain"], token).ConfigureAwait(false);
        return GitWorktreeList.Parse(output).Any(entry => entry.BranchRef == "refs/heads/" + branch
            && !string.Equals(this.paths.Resolve(entry.Path), current, StringComparison.Ordinal));
    }

    private static bool SameHead(string? actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static void Validate(GitResetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePathAndHead(request.WorktreePath, request.ExpectedHeadCommit);
        switch (request.Target)
        {
            case GitResetToNewBranch fresh:
                ValidateBranch(fresh.BranchName);
                ArgumentException.ThrowIfNullOrWhiteSpace(fresh.StartRef);
                if (fresh.StartRef.StartsWith('-') || fresh.StartRef.Contains('\0')) throw new ArgumentException("Invalid reset start ref.", nameof(request));
                break;
            case GitResetToExistingBranch existing: ValidateBranch(existing.BranchName); break;
            default: throw new ArgumentException("Unknown reset target.", nameof(request));
        }
    }

    private static void ValidatePathAndHead(string path, string head)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        if (!Path.IsPathFullyQualified(path) || head.Length is not (40 or 64) || head.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Git transitions need an absolute worktree path and exact expected HEAD.");
    }

    private static void ValidateBranch(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (branch.StartsWith('-') || branch.StartsWith("refs/", StringComparison.Ordinal) || branch.Contains('\0'))
            throw new ArgumentException("A reset target needs a branch name, not an option or full ref.", nameof(branch));
    }
}
