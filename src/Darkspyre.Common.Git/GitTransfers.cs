using static Darkspyre.Common.Git.GitOperationChecks;
using static Darkspyre.Common.Git.GitPathChecks;

namespace Darkspyre.Common.Git;

/// <summary>Explicit repository transfers, using Git's own transport/authentication and the caller's process launcher.</summary>
internal sealed partial class GitTransfers(GitCommands git, GitRepositoryReader reader,
    GitRepositoryLocks locks, IGitPathPolicy paths)
{
    private readonly GitCommands git = git;
    private readonly GitRepositoryReader reader = reader;
    private readonly GitRepositoryLocks locks = locks;
    private readonly IGitPathPolicy paths = paths;

    /// <summary>
    /// How long a remote read waits: 30 seconds. <c>ls-remote</c> moves only the ref advertisement, so a reachable remote
    /// answers in one connection and authentication; 30 seconds covers a slow SSH or HTTPS handshake and ends before
    /// macOS gives up a TCP connect at 75 seconds. A test sets a shorter one.
    /// </summary>
    internal TimeSpan RemoteReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal async Task<GitRepositorySnapshot> CloneAsync(GitCloneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (!Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("Clone requires an exact absolute destination.", nameof(request));
        if (request.RemoteUrl.Contains('\0') || request.DestinationPath.Contains('\0')
            || request.Ref is not null && (string.IsNullOrWhiteSpace(request.Ref) || request.Ref.Contains('\0')))
            throw new ArgumentException("Clone arguments must be nonempty and contain no null characters.", nameof(request));
        string destination = this.paths.Resolve(request.DestinationPath);
        await this.paths.RequireWriteAsync([destination], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(Path.Combine(destination, ".git"), cancellationToken).ConfigureAwait(false);
        if (NamedPathExists(request.DestinationPath)) throw new IOException("The clone destination already exists.");
        string workingDirectory = await this.paths.GetCloneWorkingDirectoryAsync(destination, cancellationToken).ConfigureAwait(false);
        // Branch and tag names keep ordinary clone semantics. A full ref or an exact object ID clones without a checkout,
        // fetches that revision from origin and checks it out detached, so HEAD never names a branch.
        string? revision = request.Ref is { } named && (named.StartsWith("refs/", StringComparison.Ordinal)
            || named.Length is 40 or 64 && named.All(char.IsAsciiHexDigit)) ? named : null;
        List<string> arguments = ["-c", "submodule.recurse=false", "clone", "--no-recurse-submodules", "--no-local", "--origin", "origin"];
        if (revision is not null) arguments.Add("--no-checkout");
        else if (request.Ref is { } branch) arguments.Add("--branch=" + branch);
        arguments.AddRange(["--", request.RemoteUrl, destination]);
        Check(await this.git.RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false), "clone repository");
        if (revision is not null)
        {
            Check(await this.git.RunAsync(destination,
                ["-c", "submodule.recurse=false", "fetch", "--no-recurse-submodules", "--no-tags", "origin", revision],
                cancellationToken).ConfigureAwait(false), "clone repository");
            Check(await this.git.RunAsync(destination,
                ["-c", "submodule.recurse=false", "checkout", "--no-recurse-submodules", "--detach", "FETCH_HEAD"],
                cancellationToken).ConfigureAwait(false), "clone repository");
        }
        GitRepositorySnapshot result = await this.reader.InspectAsync(destination, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Git did not leave a readable clone; no automatic cleanup was attempted.");
        if (result.WorktreePath != destination || result.CommonDirectory != this.paths.Resolve(Path.Combine(destination, ".git")))
            throw new IOException("The clone identity changed; no automatic cleanup was attempted.");
        return result;
    }
}
