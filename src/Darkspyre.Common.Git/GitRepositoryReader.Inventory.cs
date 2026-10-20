namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    internal async Task<IReadOnlyList<GitWorktreeEntry>> ReadWorktreesAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        byte[] output = await this.ReadInventoryAsync(layout.WorktreePath, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        List<GitWorktreeEntry> entries = [];
        foreach (GitWorktreeEntry entry in GitWorktreeList.Parse(output))
            if (await PermitsAsync(this.paths, entry.Path, cancellationToken).ConfigureAwait(false))
                entries.Add(entry with { Path = this.paths.Resolve(entry.Path) });
        return entries.AsReadOnly();
    }

    internal async Task<IReadOnlyList<GitBranchEntry>> ReadLocalBranchesAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        byte[] output = await this.ReadInventoryAsync(layout.WorktreePath,
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

    /// <summary>
    /// The local branches and origin's remote-tracking branches from one <c>for-each-ref</c>: each record is five fields,
    /// ref, commit, symref, upstream and worktree path, joined by NUL and ended by a newline. A ref name holds no newline,
    /// so the last newline before the next record's ref ends a worktree path that contains one. Symbolic refs, such as
    /// <c>origin/HEAD</c>, are left out; a worktree path the path policy refuses is reported as null.
    /// </summary>
    internal async Task<IReadOnlyList<GitBranchReference>> ReadBranchesAsync(string repository, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(repository, cancellationToken).ConfigureAwait(false);
        byte[] output = await this.ReadInventoryAsync(layout.WorktreePath,
            ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)%00%(upstream)%00%(worktreepath)",
                "refs/heads/", "refs/remotes/origin/"], cancellationToken).ConfigureAwait(false);
        if (output.Length == 0) return [];
        string[] fields = GitPaths.Decode(output).Split('\0');
        if ((fields.Length - 1) % 4 != 0 || !fields[^1].EndsWith('\n')) throw InvalidBranch();
        List<GitBranchReference> branches = [];
        string refName = fields[0];
        for (int index = 1; index < fields.Length; index += 4)
        {
            string last = fields[index + 3];
            int end = last.LastIndexOf('\n');
            if (end < 0) throw InvalidBranch();
            string worktree = last[..end];
            string commit = fields[index];
            if (!(refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    || refName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
                || !GitPaths.IsObjectId(commit, layout.ObjectFormat))
                throw InvalidBranch();
            if (fields[index + 1].Length == 0)
            {
                string? checkedOut = worktree.Length != 0 && await PermitsAsync(this.paths, worktree, cancellationToken).ConfigureAwait(false)
                    ? this.paths.Resolve(worktree) : null;
                branches.Add(new(refName, commit, fields[index + 2].Length == 0 ? null : fields[index + 2], checkedOut));
            }
            refName = last[(end + 1)..];
        }
        if (refName.Length != 0) throw InvalidBranch();
        return branches.AsReadOnly();
    }

    private static InvalidDataException InvalidBranch() => new("Git returned an invalid branch entry.");

    private async Task<byte[]> ReadInventoryAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        GitRunResult result = await this.RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        GitOperationChecks.Check(result, "read inventory");
        // for-each-ref can omit a broken ref, emit a warning and still exit zero. That cannot prove pruning.
        if (result.Errors.Length != 0) throw new IOException("Git could not read a complete inventory.");
        return result.Output;
    }

}
