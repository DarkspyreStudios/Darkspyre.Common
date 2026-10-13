namespace Darkspyre.Common.Git;

/// <summary>One record of <c>git worktree list --porcelain</c>.</summary>
/// <param name="Path">The worktree's path as Git printed it.</param>
/// <param name="BranchRef">The full ref of the checked-out branch, or null when HEAD is detached or the worktree is bare.</param>
internal sealed record GitWorktreeEntry(string Path, string? BranchRef);

/// <summary>The one parser of <c>git worktree list --porcelain</c> output.</summary>
internal static class GitWorktreeList
{
    /// <summary>
    /// The records of <paramref name="output"/>: records are separated by an empty line, <c>worktree &lt;path&gt;</c> starts
    /// a record, <c>branch &lt;ref&gt;</c> sets its branch, and other lines are ignored. A path that holds a newline is
    /// unsupported: it parses as a wrong path.
    /// </summary>
    internal static IReadOnlyList<GitWorktreeEntry> Parse(byte[] output)
    {
        ArgumentNullException.ThrowIfNull(output);
        List<GitWorktreeEntry> entries = [];
        string? path = null;
        string? branch = null;
        foreach (string line in GitPaths.Decode(output).Split('\n'))
        {
            if (line.Length == 0)
            {
                if (path is not null) entries.Add(new(path, branch));
                path = null;
                branch = null;
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (path is not null) entries.Add(new(path, branch));
                path = line["worktree ".Length..];
                branch = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
        }
        if (path is not null) entries.Add(new(path, branch));
        return entries.AsReadOnly();
    }
}
