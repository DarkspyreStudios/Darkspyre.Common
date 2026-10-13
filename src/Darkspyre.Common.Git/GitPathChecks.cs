namespace Darkspyre.Common.Git;

/// <summary>Path checks the Git library and its consumers share.</summary>
public static class GitPathChecks
{
    /// <summary>
    /// Whether a directory entry exists at <paramref name="path"/>, without following a final link: an existing dangling
    /// link counts, so a new clone, worktree or workspace never takes its place.
    /// </summary>
    public static bool NamedPathExists(string path)
    {
        // FileAccess resolves to the canonical target. Creation also needs the literal directory
        // entry check: an existing dangling symlink is not a new destination.
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
}
