using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    internal async Task<GitPreservationCheck> CheckPreservationAsync(string worktreePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        GitRepositorySnapshot? observed = null;
        List<string> blockers = [];
        string[] commits = [];
        try
        {
            observed = await this.InspectAsync(worktreePath, cancellationToken).ConfigureAwait(false);
            if (observed is null) blockers.Add("The path is not a Git worktree.");
            else
            {
                string requested = this.paths.Resolve(worktreePath);
                if (!SamePath(requested, observed.WorktreePath)) blockers.Add("The supplied path is not the worktree root.");
                string ownGitDirectory = this.paths.Resolve(Line(await this.ReadAsync(observed.WorktreePath,
                    ["rev-parse", "--path-format=absolute", "--git-dir"], cancellationToken).ConfigureAwait(false)));
                if (SamePath(ownGitDirectory, observed.CommonDirectory)) blockers.Add("The primary repository is not a removable linked worktree.");
                if (observed.HeadCommit is null) blockers.Add("The worktree has no established HEAD commit.");
                else
                {
                    // --all also includes HEAD, making a detached, unreferenced commit look safe.
                    // Exclude per-worktree refs too: their storage disappears with the worktree.
                    byte[] unpreserved = await this.ReadAsync(observed.WorktreePath,
                        ["rev-list", observed.HeadCommit, "--not",
                            "--exclude=refs/bisect/*", "--exclude=refs/worktree/*", "--exclude=refs/rewritten/*", "--glob=refs/*"],
                        cancellationToken).ConfigureAwait(false);
                    commits = Utf8.GetString(unpreserved).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (commits.Any(commit => commit.Length is not (40 or 64) || commit.Any(character => !char.IsAsciiHexDigit(character))))
                        throw new InvalidDataException("Git returned invalid reachability evidence.");
                    if (commits.Length != 0) blockers.Add("Commits are not reachable from a retained shared reference.");
                }
                // Even a visually clean submodule can contain ignored/private state that the outer
                // worktree's status cannot prove preserved. No recursive deletion is inferred here.
                byte[] index = await this.ReadAsync(observed.WorktreePath, ["ls-files", "--stage", "-z"], cancellationToken).ConfigureAwait(false);
                int offset = 0;
                while (offset < index.Length)
                    if (Field(index, ref offset).StartsWith("160000 ", StringComparison.Ordinal))
                    {
                        blockers.Add("Submodule contents require separate preservation checks.");
                        break;
                    }
                GitRunResult status = await this.RunAsync(observed.WorktreePath,
                    ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=traditional"], cancellationToken).ConfigureAwait(false);
                Check(status, "verify preservation status");
                if (status.Errors.Length != 0) blockers.Add("Git reported uncertainty while reading filesystem status.");
                var finalStatus = ParseStatus(status.Output);
                if (!observed.Status.SequenceEqual(finalStatus)) blockers.Add("Worktree state changed during the preservation check.");
                if (observed.HeadCommit is not null)
                {
                    string finalHead = await this.ResolveCommitAsync(observed.WorktreePath, "HEAD", cancellationToken).ConfigureAwait(false);
                    if (finalHead != observed.HeadCommit) blockers.Add("HEAD changed during the preservation check.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Uncertainty is a retention reason, never an invented clean checkout. Cancellation and
            // invalid caller arguments still propagate normally; no stderr/path payload is copied.
            blockers.Add($"Preservation could not be established ({error.GetType().Name}).");
        }
        string[] tracked = observed?.Status.Where(entry => !entry.IsUntracked && !entry.IsIgnored).Select(entry => entry.Path).ToArray() ?? [];
        string[] untracked = observed?.Status.Where(entry => entry.IsUntracked).Select(entry => entry.Path).ToArray() ?? [];
        string[] ignored = observed?.Status.Where(entry => entry.IsIgnored).Select(entry => entry.Path).ToArray() ?? [];
        if (tracked.Length != 0) blockers.Add("Tracked changes are not committed.");
        if (untracked.Length != 0) blockers.Add("Untracked files are not preserved.");
        bool canReset = blockers.Count == 0;
        if (ignored.Length != 0) blockers.Add(GitPreservationCheck.IgnoredFilesBlocker);
        return new(observed?.HeadCommit, observed?.BranchRef, Array.AsReadOnly(tracked), Array.AsReadOnly(untracked),
            Array.AsReadOnly(ignored), Array.AsReadOnly(commits), blockers.AsReadOnly(), blockers.Count == 0, canReset);
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);
}
