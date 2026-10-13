using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitWorktrees
{
    internal async Task<PreparedGitWorktree> PrepareAsync(string repositoryPath,
        string sourceRef, string branchName, CancellationToken cancellationToken = default)
    {
        ValidateBranchName(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        GitRepositoryReader.GitLayout current = await this.reader.LocateAsync(repositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The source repository is unavailable.");
        await this.paths.RequireWriteAsync([current.CommonDirectory], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(current.CommonDirectory, cancellationToken).ConfigureAwait(false);
        return await this.PrepareUnderLockAsync(current, sourceRef, branchName, cancellationToken).ConfigureAwait(false);
    }

    // Under the gate: the branch must not exist yet, and the source resolves once.
    private async Task<PreparedGitWorktree> PrepareUnderLockAsync(GitRepositoryReader.GitLayout current,
        string sourceRef, string branchName, CancellationToken cancellationToken)
    {
        string branchRef = "refs/heads/" + branchName;
        Check(await this.git.RunAsync(current.WorktreePath, ["check-ref-format", branchRef], cancellationToken).ConfigureAwait(false), "validate branch name");
        GitRunResult existing = await this.git.RunAsync(current.WorktreePath, ["show-ref", "--verify", "--quiet", branchRef], cancellationToken).ConfigureAwait(false);
        if (existing.Exit.ExitCode == 0) throw new IOException("The requested branch already exists.");
        if (existing.Exit.ExitCode != 1) Check(existing, "check branch availability");
        string commit = await this.reader.ResolveCommitAsync(current.WorktreePath, sourceRef, cancellationToken).ConfigureAwait(false);
        return new(current.WorktreePath, current.CommonDirectory, sourceRef, commit, branchName);
    }

    private static void ValidateBranchName(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (branchName.StartsWith('-') || branchName.StartsWith("refs/", StringComparison.Ordinal))
            throw new ArgumentException("Supply a new branch name, not an option or full ref.", nameof(branchName));
    }
}
