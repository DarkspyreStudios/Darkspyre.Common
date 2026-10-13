using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    private const int RemoteRefLimit = 64;

    /// <summary>
    /// The configured remotes and their fetch and push destinations after Git's URL rewriting, as structured endpoints,
    /// read in the caller's authorized directory. Every child's output is live-only, raw configuration and destination
    /// bytes are zeroed after parsing, and no remote is contacted.
    /// </summary>
    internal async Task<IReadOnlyList<GitRemoteInfo>> ReadRemoteEndpointsAsync(string directory,
        CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        GitRunResult configuration = await this.RunAsync(worktree,
            ["config", "--null", "--get-regexp", @"^remote\..*\.(url|pushurl)$"], cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GitRemoteConfiguration> remotes;
        try
        {
            // Exit 1 is Git's answer for a key that is not configured.
            if (configuration.Exit is { ExitCode: 1, Diagnostic: null }) return [];
            Check(configuration, "read the remote configuration");
            remotes = GitRemoteEndpoints.ParseConfiguration(configuration.Output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(configuration.Output);
        }
        List<GitRemoteInfo> result = [];
        foreach (GitRemoteConfiguration remote in remotes)
        {
            IReadOnlyList<GitRemoteEndpoint> fetch = remote.Urls == 0 ? []
                : remote.LineBreak ? Unrecognized(remote.Urls)
                : await this.ReadDestinationsAsync(worktree, ["remote", "get-url", "--all", "--", remote.Name], cancellationToken)
                    .ConfigureAwait(false);
            // A value with a line break cannot be told apart in Git's line-per-URL output, so its remote's endpoints are
            // never guessed: push uses the push URLs when any are configured, and the fetch URLs otherwise.
            IReadOnlyList<GitRemoteEndpoint> push = remote.LineBreak ? Unrecognized(remote.PushUrls > 0 ? remote.PushUrls : remote.Urls)
                : await this.ReadDestinationsAsync(worktree, ["remote", "get-url", "--push", "--all", "--", remote.Name],
                    cancellationToken).ConfigureAwait(false);
            result.Add(new(remote.Name, fetch, push));
        }
        return result;
    }

    private async Task<IReadOnlyList<GitRemoteEndpoint>> ReadDestinationsAsync(string worktree,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        GitRunResult result = await this.RunAsync(worktree, arguments, cancellationToken).ConfigureAwait(false);
        try
        {
            Check(result, "read the remote destinations");
            return await GitRemoteEndpoints.ParseDestinationsAsync(result.Output, worktree, this.paths, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(result.Output);
        }
    }

    private static IReadOnlyList<GitRemoteEndpoint> Unrecognized(int count) => [.. Enumerable.Repeat(GitRemoteEndpoints.Unrecognized, count)];

    /// <summary>
    /// Reads the requested refs from the remote's single push destination with <c>ls-remote --refs</c>, which changes no
    /// repository state and so takes no gate. When the push URL equals the fetch URL the command names the remote;
    /// otherwise it names the push URL. Only a line whose ref equals a requested ref counts, because <c>ls-remote</c>
    /// matches patterns against ref tails; a requested ref with no line is confirmed absent. The URL never enters a
    /// result or a diagnostic; a launcher that refuses the start owns its own refusal text.
    /// </summary>
    internal async Task<GitRemoteRefsResult> ReadRemoteRefsAsync(GitRemoteRefsRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Refs);
        RequireRemote(request.Remote);
        string[] refs = [.. request.Refs];
        if (refs.Length is 0 or > RemoteRefLimit || refs.Any(reference => string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith("refs/", StringComparison.Ordinal) || reference.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0))
            throw new ArgumentException("A remote read needs 1 to 64 full ref names that start with refs/.", nameof(request));
        GitLayout layout = await this.LocateAsync(request.Repository, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The repository is unavailable.");
        string worktree = layout.WorktreePath;
        string push = await this.ReadPushUrlAsync(worktree, request.Remote, cancellationToken).ConfigureAwait(false);
        string fetch = Line(await this.ReadAsync(worktree, ["remote", "get-url", "--", request.Remote], cancellationToken)
            .ConfigureAwait(false));
        bool named = string.Equals(push, fetch, StringComparison.Ordinal);
        string destination = named ? request.Remote : push;
        if (destination.StartsWith('-')) throw new ArgumentException("The remote's push URL cannot be passed to Git as a destination.", nameof(request));
        GitRunResult result = await this.RunAsync(worktree, ["ls-remote", "--refs", "--end-of-options", destination, .. refs], timeout,
            default, cancellationToken).ConfigureAwait(false);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        if (result.TimedOut)
            return new(observedAt, [], GitRemoteReadOutcome.TimedOut,
                string.Create(CultureInfo.InvariantCulture, $"The remote did not answer within {timeout.TotalSeconds:0} seconds."));
        if (result.Exit.ExitCode != 0 || result.Exit.Diagnostic is not null)
            return new(observedAt, [], GitRemoteReadOutcome.Failed, "Git could not read the remote's refs.");
        Dictionary<string, string> advertised = new(StringComparer.Ordinal);
        string text;
        try { text = Utf8.GetString(result.Output); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Git returned remote refs that are not UTF-8."); }
        if (text.Length != 0 && !text.EndsWith('\n')) throw new InvalidDataException("Git returned an incomplete remote ref.");
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 2 || !GitPaths.IsObjectId(fields[0], layout.ObjectFormat))
                throw new InvalidDataException("Git returned an invalid remote ref.");
            if (refs.Contains(fields[1], StringComparer.Ordinal)) advertised[fields[1]] = fields[0];
        }
        GitRemoteRef[] read = [.. refs.Select(reference => new GitRemoteRef(reference, advertised.GetValueOrDefault(reference)))];
        return new(observedAt, Array.AsReadOnly(read), GitRemoteReadOutcome.Confirmed);
    }

    /// <summary>
    /// The remote's single push destination after Git's URL rewriting, read with live-only output. A remote with more or
    /// fewer than one push destination throws <see cref="NotSupportedException"/>. The URL stays inside the host.
    /// </summary>
    internal async Task<string> ReadPushUrlAsync(string worktree, string remote, CancellationToken cancellationToken)
    {
        RequireRemote(remote);
        GitRunResult result = await this.RunAsync(worktree, ["remote", "get-url", "--push", "--all", "--", remote],
            cancellationToken).ConfigureAwait(false);
        Check(result, "resolve configured push remote");
        string[] urls;
        try { urls = Utf8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Git returned a push destination that is not UTF-8."); }
        return urls.Length == 1 ? urls[0]
            : throw new NotSupportedException("Push requires one configured destination URL; use a remote with a single push destination.");
    }

    private static void RequireRemote(string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        if (remote.StartsWith('-') || remote.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new ArgumentException("A configured remote name is required.", nameof(remote));
    }
}
