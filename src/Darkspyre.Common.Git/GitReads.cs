namespace Darkspyre.Common.Git;

/// <summary>A repository's object format. It sets the width of every object ID the repository uses.</summary>
public enum GitObjectFormat
{
    /// <summary>SHA-1: object IDs are 40 lowercase hexadecimal digits.</summary>
    Sha1,
    /// <summary>SHA-256: object IDs are 64 lowercase hexadecimal digits.</summary>
    Sha256,
}

/// <summary>How a configured remote destination is reached.</summary>
public enum GitRemoteTransport
{
    /// <summary>An <c>https://</c> URL.</summary>
    Https,
    /// <summary>An <c>http://</c> URL.</summary>
    Http,
    /// <summary>An <c>ssh://</c> URL.</summary>
    Ssh,
    /// <summary>The SCP-like form <c>host:path</c>.</summary>
    Scp,
    /// <summary>A local path or a <c>file://</c> URL.</summary>
    File,
    /// <summary>Any other form, such as a remote helper or <c>git://</c>.</summary>
    Unrecognized,
}

/// <summary>Whether the host could describe a remote destination.</summary>
public enum GitRemoteIdentityStatus
{
    /// <summary>The endpoint's fields describe the destination.</summary>
    Parsed,
    /// <summary>
    /// The host cannot describe the destination without exposing what it must not, such as a user name, a query or a path
    /// outside the caller's scope. The endpoint's fields are null; the caller associates it another way.
    /// </summary>
    RequiresAssociation,
}

/// <summary>
/// One remote destination as structured fields, never the raw URL. User information is never included. The host
/// normalizes an HTTP host to lowercase ASCII and keeps the port and path as configured; it does not decide whether two
/// endpoints name the same repository.
/// </summary>
/// <param name="Transport">How the destination is reached.</param>
/// <param name="Status">Whether the fields describe the destination.</param>
/// <param name="Host">The host, or null for a local destination or one that requires association.</param>
/// <param name="Port">The port when the URL names one explicitly, otherwise null.</param>
/// <param name="RepositoryPath">
/// The repository path as configured: escaped for HTTP, with its leading slash or its absence kept for SSH and SCP, and
/// the canonical local path for a local destination the caller may read. Null when the endpoint requires association.
/// </param>
public sealed record GitRemoteEndpoint(
    GitRemoteTransport Transport,
    GitRemoteIdentityStatus Status,
    string? Host,
    int? Port,
    string? RepositoryPath);

/// <summary>One configured remote and its destinations, in Git's order, after Git's URL rewriting.</summary>
/// <param name="Name">The remote's name.</param>
/// <param name="Fetch">The destinations a fetch from this remote uses.</param>
/// <param name="Push">The destinations a push to this remote uses. They can differ from <paramref name="Fetch"/>.</param>
public sealed record GitRemoteInfo(
    string Name,
    IReadOnlyList<GitRemoteEndpoint> Fetch,
    IReadOnlyList<GitRemoteEndpoint> Push);

/// <summary>What a commit resolution found.</summary>
public enum GitCommitResolutionStatus
{
    /// <summary>The reference names a local commit; its ID is set.</summary>
    Resolved,
    /// <summary>The reference resolves to no local commit, including HEAD on a branch with no commits.</summary>
    UnresolvedReference,
}

/// <summary>The result of resolving a reference once.</summary>
/// <param name="Status">What the resolution found.</param>
/// <param name="ObjectFormat">The repository's object format.</param>
/// <param name="CommitId">The commit, only when the status is Resolved.</param>
public sealed record GitCommitResolution(
    GitCommitResolutionStatus Status,
    GitObjectFormat ObjectFormat,
    string? CommitId);

/// <summary>The kind of an entry. No entry is a directory.</summary>
public enum GitEntryKind
{
    /// <summary>An ordinary file, mode <c>100644</c>.</summary>
    RegularFile,
    /// <summary>An executable file, mode <c>100755</c>.</summary>
    ExecutableFile,
    /// <summary>A symbolic link, mode <c>120000</c>. Its blob holds the link text.</summary>
    SymbolicLink,
    /// <summary>A submodule commit, mode <c>160000</c>. It is not a blob and cannot be opened.</summary>
    GitLink,
}

/// <summary>Why a Git read failed.</summary>
public enum GitReadFailure
{
    /// <summary>The path names a directory or a submodule commit, not a file.</summary>
    NotBlob,
    /// <summary>The file is longer than the caller's limit. No content was read.</summary>
    LimitExceeded,
    /// <summary>The object is not in the local object store, as in a partial clone. Nothing was fetched.</summary>
    ObjectMissing,
}

/// <summary>
/// A Git read failed for the reason <see cref="Failure"/> names. The message is written by the host: it never
/// contains Git's output, a remote URL, file content or a credential.
/// </summary>
public sealed class GitReadException : IOException
{
    /// <summary>Creates the exception for <paramref name="failure"/> with a host-written message.</summary>
    /// <param name="failure">Why the read failed.</param>
    /// <param name="message">A safe description of the failure.</param>
    public GitReadException(GitReadFailure failure, string message) : base(message) => this.Failure = failure;

    /// <summary>Why the read failed.</summary>
    public GitReadFailure Failure { get; }
}

/// <summary>One local branch observed in the repository's ref inventory.</summary>
/// <param name="FullRefName">The full branch name under refs/heads/.</param>
/// <param name="CommitId">The exact local commit ID at the time of the read.</param>
public sealed record GitBranchEntry(string FullRefName, string CommitId);

/// <summary>Whether Git read commands may use the repository's configured transports.</summary>
public enum GitReadPolicy
{
    /// <summary>Uses the repository's normal read behavior, including implicit fetches where Git permits them.</summary>
    Configured,
    /// <summary>Forbids all Git transports during reads; unavailable local objects fail the read.</summary>
    LocalOnly,
}
