namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    internal async Task<IReadOnlyList<GitWorktreeEntry>> ReadWorktreesAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        byte[] output = await this.ReadAsync(layout.WorktreePath, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        List<GitWorktreeEntry> entries = [];
        foreach (GitWorktreeEntry entry in GitWorktreeList.Parse(output))
            if (await PermitsAsync(this.paths, entry.Path, cancellationToken).ConfigureAwait(false))
                entries.Add(entry with { Path = this.paths.Resolve(entry.Path) });
        return entries.AsReadOnly();
    }

    internal async Task<IReadOnlyList<GitBranchEntry>> ReadLocalBranchesAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        byte[] output = await this.ReadAsync(layout.WorktreePath,
            ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads/"], cancellationToken).ConfigureAwait(false);
        if (output.Length == 0) return [];
        List<GitBranchEntry> branches = [];
        foreach (string line in Text(output).Split('\n'))
        {
            int separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator < 0 || !GitPaths.IsObjectId(line.AsSpan(0, separator), layout.ObjectFormat)
                || !line.AsSpan(separator + 1).StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new InvalidDataException("Git returned an invalid local branch entry.");
            branches.Add(new(line[(separator + 1)..], line[..separator]));
        }
        return branches.AsReadOnly();
    }
}
