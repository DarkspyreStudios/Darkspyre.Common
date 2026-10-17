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

    // Under the gate: the branch must not exist yet, and the source resolves once. A repository with no commits has
    // nothing to resolve, so the branch starts with none whatever the source names.
    private async Task<PreparedGitWorktree> PrepareUnderLockAsync(GitRepositoryReader.GitLayout current,
        string sourceRef, string branchName, CancellationToken cancellationToken)
    {
        bool hasCommits = await this.reader.HasCommitsAsync(current.WorktreePath, cancellationToken).ConfigureAwait(false);
        await this.CheckNewBranchAsync(current, branchName, cancellationToken).ConfigureAwait(false);
        string? commit = hasCommits
            ? await this.reader.ResolveCommitAsync(current.WorktreePath, sourceRef, cancellationToken).ConfigureAwait(false)
            : null;
        return new(current.WorktreePath, current.CommonDirectory, sourceRef, commit, branchName);
    }

    // Under the gate: the branch name is valid and no branch has it yet.
    private async Task CheckNewBranchAsync(GitRepositoryReader.GitLayout repository, string branchName,
        CancellationToken cancellationToken)
    {
        string branchRef = "refs/heads/" + branchName;
        Check(await this.git.RunAsync(repository.WorktreePath, ["check-ref-format", branchRef], cancellationToken).ConfigureAwait(false), "validate branch name");
        GitRunResult existing = await this.git.RunAsync(repository.WorktreePath, ["show-ref", "--verify", "--quiet", branchRef], cancellationToken).ConfigureAwait(false);
        if (existing.Exit.ExitCode == 0) throw new IOException("The requested branch already exists.");
        if (existing.Exit.ExitCode != 1) Check(existing, "check branch availability");
    }

    private static void ValidateBranchName(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (branchName.StartsWith('-') || branchName.StartsWith("refs/", StringComparison.Ordinal))
            throw new ArgumentException("Supply a new branch name, not an option or full ref.", nameof(branchName));
    }
}
