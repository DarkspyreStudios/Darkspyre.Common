using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitTransfers
{
    /// <summary>
    /// Pushes one resolved commit to one remote branch under the repository's operation gate. A lease push replaces the
    /// remote branch only while it names <see cref="GitPushRequest.ExpectedRemoteOid"/>. Any outcome the host cannot
    /// confirm, including a cancellation after dispatch, is Unknown; the host never retries.
    /// </summary>
    internal async Task<GitPushResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationBranchRef);
        if (request.RemoteName.StartsWith('-') || request.RemoteName.Contains('\0')
            || !request.DestinationBranchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new ArgumentException("Push requires a configured remote and an exact destination branch ref.", nameof(request));
        if (!Enum.IsDefined(request.Mode) || request.Mode == GitPushMode.Normal && request.ExpectedRemoteOid is not null
            || request.ExpectedRemoteOid is { } remoteOid && !ObjectId(remoteOid))
            throw new ArgumentException("Only a lease push takes an expected remote commit, and every expected commit is a full ID.", nameof(request));
        GitRepositoryReader.GitLayout current = await this.reader.LocateAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The push repository is unavailable.");
        await this.paths.RequireWriteAsync([current.CommonDirectory], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(current.CommonDirectory, cancellationToken).ConfigureAwait(false);
        Check(await this.git.RunAsync(current.WorktreePath, ["check-ref-format", request.DestinationBranchRef],
            cancellationToken).ConfigureAwait(false), "validate destination branch");
        _ = await this.reader.ReadPushUrlAsync(current.WorktreePath, request.RemoteName, cancellationToken).ConfigureAwait(false);
        string commit = await this.reader.ResolveCommitAsync(current.WorktreePath, request.Source, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // An empty expected value makes the lease require the remote branch to be absent.
        string lease = request.Mode == GitPushMode.Lease
            ? "--force-with-lease=" + request.DestinationBranchRef + ":" + request.ExpectedRemoteOid?.ToLowerInvariant()
            : "--no-force-with-lease";
        GitRunResult result;
        try
        {
            result = await this.git.RunAsync(current.WorktreePath,
                ["-c", $"remote.{request.RemoteName}.mirror=false", "-c", "push.autoSetupRemote=false",
                    "push", "--porcelain", "--no-mirror", "--no-all", "--no-follow-tags", "--no-force",
                    lease, "--no-force-if-includes", "--recurse-submodules=no", "--", request.RemoteName,
                    commit + ":" + request.DestinationBranchRef], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // After dispatch, cancellation/transport failure does not establish remote rejection.
            // The shared process path settles its child; this operation never retries the push.
            return UnknownPush(request.DestinationBranchRef);
        }
        return ReadPushResult(request.DestinationBranchRef, request.Mode, result);
    }

    /// <summary>Reads refs from the remote's push destination.</summary>
    internal Task<GitRemoteRefsResult> ReadRemoteRefsAsync(GitRemoteRefsRequest request,
        CancellationToken cancellationToken = default) =>
        this.reader.ReadRemoteRefsAsync(request, this.RemoteReadTimeout, cancellationToken);

    private static GitPushResult ReadPushResult(string destination, GitPushMode mode, GitRunResult result)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(result.Output); }
        catch (DecoderFallbackException) { return UnknownPush(destination); }
        if (text.Length != 0 && !text.EndsWith('\n')) return UnknownPush(destination);
        GitPushOutcome? outcome = null;
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Ignore Git's To/Done lines, which may contain a credential-bearing URL. Never copy
            // a remote hook's free-form reason into an SDK diagnostic.
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || fields[0].Length != 1) continue;
            int separator = fields[1].LastIndexOf(':');
            if (separator < 0 || fields[1][(separator + 1)..] != destination) continue;
            if (outcome is not null) return UnknownPush(destination);
            outcome = fields[0][0] switch
            {
                ' ' or '*' or '=' => GitPushOutcome.Accepted,
                // A forced update is what a lease push asks for; a normal push never forces, so there it is unexplained.
                '+' when mode == GitPushMode.Lease => GitPushOutcome.Accepted,
                '!' when fields[2].StartsWith("[rejected]", StringComparison.Ordinal)
                    || fields[2].StartsWith("[remote rejected]", StringComparison.Ordinal) => GitPushOutcome.Rejected,
                _ => GitPushOutcome.Unknown,
            };
        }
        if (outcome is null or GitPushOutcome.Unknown) return UnknownPush(destination);
        string? diagnostic = outcome == GitPushOutcome.Rejected ? "Git rejected the requested branch update."
            : result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null
                ? "Git acknowledged the ref update but did not complete cleanly; inspect remote state before another action." : null;
        return new(Array.AsReadOnly(new[] { new GitPushRefResult(destination, outcome.Value, diagnostic) }), diagnostic);
    }

    private static GitPushResult UnknownPush(string destination)
    {
        const string diagnostic = "Remote acceptance was not confirmed. Inspect remote state before another action; no automatic retry was attempted.";
        return new(Array.AsReadOnly(new[] { new GitPushRefResult(destination, GitPushOutcome.Unknown, diagnostic) }), diagnostic);
    }
}
