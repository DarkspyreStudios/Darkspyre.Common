namespace Darkspyre.Common.Git;

/// <summary>
/// The caller's view of the file system: how paths compare and which paths the caller may read and change. The library
/// asks it before any Git child starts for a caller's directory, and again for each path a read returns.
/// </summary>
public interface IGitPathPolicy
{
    /// <summary>The absolute path <paramref name="path"/> really names, as the caller compares paths.</summary>
    string Resolve(string path);
    /// <summary>Whether the caller may read <paramref name="absolutePath"/>, and what is there now.</summary>
    ValueTask<GitPathAccess> GetReadAccessAsync(string absolutePath, CancellationToken cancellationToken);
    /// <summary>Throws <see cref="UnauthorizedAccessException"/> with the reason when the caller may not change every path.</summary>
    ValueTask RequireWriteAsync(IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken);
    /// <summary>
    /// The absolute directory <c>git clone</c> runs in for a clone into <paramref name="destination"/>. Clone asks for it
    /// after the write check and the destination check, and before Git starts. The directory must exist.
    /// </summary>
    ValueTask<string> GetCloneWorkingDirectoryAsync(string destination, CancellationToken cancellationToken);
}

/// <summary>What a read-access check found.</summary>
public enum GitPathAccess
{
    /// <summary>The caller may not read the path.</summary>
    Refused,
    /// <summary>The caller may read the path, and nothing is there now.</summary>
    Absent,
    /// <summary>The caller may read the path, and a file or link is there.</summary>
    File,
    /// <summary>The caller may read the path, and a directory is there.</summary>
    Directory,
}
