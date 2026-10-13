namespace Darkspyre.Common.Git;

/// <summary>One record of <c>git worktree list --porcelain</c>.</summary>
/// <param name="Path">The worktree's path as Git printed it.</param>
/// <param name="BranchRef">The full ref of the checked-out branch, or null when HEAD is detached or the worktree is bare.</param>
/// <param name="HeadCommit">The observed commit, or null for an unborn branch or a bare entry.</param>
/// <param name="IsBare">Whether this entry names a bare repository.</param>
public sealed record GitWorktreeEntry(string Path, string? BranchRef, string? HeadCommit, bool IsBare);

/// <summary>The one parser of <c>git worktree list --porcelain</c> output.</summary>
internal static class GitWorktreeList
{
    /// <summary>
    /// The records of <paramref name="output"/>: records are separated by an empty line, <c>worktree &lt;path&gt;</c> starts
    /// a record, <c>branch &lt;ref&gt;</c> sets its branch, and <c>HEAD</c> identifies the observed commit. Unsupported
    /// or incomplete output throws; a failed inventory never claims that worktrees disappeared.
    /// </summary>
    internal static IReadOnlyList<GitWorktreeEntry> Parse(byte[] output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length == 0 || output[^1] != '\n')
            throw new InvalidDataException("Git returned an incomplete worktree inventory.");
        List<GitWorktreeEntry> entries = [];
        string? path = null;
        string? branch = null;
        string? head = null;
        bool bare = false;
        void Add()
        {
            if (path is null) return;
            if (!System.IO.Path.IsPathFullyQualified(path) || !bare && head is null)
                throw new InvalidDataException("Git returned an incomplete worktree entry.");
            entries.Add(new(path, branch, head is null || head.All(c => c == '0') ? null : head, bare));
            path = null;
            branch = null;
            head = null;
            bare = false;
        }
        foreach (string line in GitPaths.Decode(output).Split('\n'))
        {
            if (line.Length == 0)
            {
                Add();
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Add();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line["branch ".Length..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
                if (head.Length is not (40 or 64) || !head.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("Git returned an invalid worktree commit.");
            }
            else if (line == "bare") bare = true;
            else if (line != "detached" && line != "locked" && line != "prunable"
                && !line.StartsWith("locked ", StringComparison.Ordinal) && !line.StartsWith("prunable ", StringComparison.Ordinal))
                throw new InvalidDataException("Git returned an unsupported worktree entry.");
        }
        Add();
        return entries.AsReadOnly();
    }
}
