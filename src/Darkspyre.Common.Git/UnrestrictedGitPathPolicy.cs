namespace Darkspyre.Common.Git;

/// <summary>
/// Every path is readable and writable. <see cref="Resolve"/> returns Path.GetFullPath(path) with its longest existing
/// prefix replaced by that prefix's target, following a link in every component
/// (FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true) component by component), and keeps the missing names after
/// it as given, as the host's PosixPathRules.Resolve does.
/// </summary>
/// <gotcha>It does not normalize letter case on a case-insensitive volume; Git does not either.</gotcha>
public sealed class UnrestrictedGitPathPolicy : IGitPathPolicy
{
    private UnrestrictedGitPathPolicy()
    {
    }

    /// <summary>The one instance.</summary>
    public static UnrestrictedGitPathPolicy Instance { get; } = new();

    /// <inheritdoc />
    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string current = Path.GetPathRoot(full) ?? throw new ArgumentException("Path resolution requires an absolute path.", nameof(path));
        string[] components = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            string candidate = Path.Join(current, components[index]);
            // A missing entry, a dangling link and a link loop end the existing prefix: the rest stays as given.
            if (!Path.Exists(candidate)) return Path.Join([current, .. components[index..]]);
            current = File.ResolveLinkTarget(candidate, returnFinalTarget: true) is { } target
                ? this.Resolve(target.FullName)
                : candidate;
        }
        return current;
    }

    /// <inheritdoc />
    public ValueTask<GitPathAccess> GetReadAccessAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(absolutePath)) return ValueTask.FromResult(GitPathAccess.Directory);
        return ValueTask.FromResult(GitPathChecks.NamedPathExists(absolutePath) ? GitPathAccess.File : GitPathAccess.Absent);
    }

    /// <inheritdoc />
    public ValueTask RequireWriteAsync(IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>The nearest existing ancestor of the destination: its parent when that exists, else the first existing directory above it.</summary>
    public ValueTask<string> GetCloneWorkingDirectoryAsync(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The destination is fully qualified, so its root exists and the walk ends.
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("A clone destination needs a parent directory.", nameof(destination));
        while (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory)
                ?? throw new DirectoryNotFoundException("No directory above the clone destination exists.");
        return ValueTask.FromResult(directory);
    }
}
