using System.Text;
using System.Collections.ObjectModel;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitTransfers
{
    internal async Task<GitFetchResult> FetchAsync(GitFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteName);
        ArgumentNullException.ThrowIfNull(request.Refs);
        string[] refs = request.Refs.ToArray();
        if (request.RemoteName.StartsWith('-') || request.RemoteName.Contains('\0') || refs.Length == 0)
            throw new ArgumentException("Fetch requires a configured remote and an explicit nonempty ref set.", nameof(request));
        foreach (string reference in refs)
        {
            if (string.IsNullOrWhiteSpace(reference) || reference.IndexOfAny(['\0', '*', '?', '[', '\\']) >= 0
                || reference.StartsWith('-') || reference.StartsWith('+') || reference.StartsWith('^'))
                throw new ArgumentException("Fetch refs must be exact sources or source:destination mappings, without force or patterns.", nameof(request));
            string[] parts = reference.Split(':');
            if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace)
                || parts.Length == 2 && !parts[1].StartsWith("refs/", StringComparison.Ordinal))
                throw new ArgumentException("A fetch mapping needs a nonempty source and a full destination ref.", nameof(request));
        }
        GitRepositoryReader.GitLayout current = await this.reader.LocateAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryNotFoundException("The fetch repository is unavailable.");
        await this.paths.RequireWriteAsync([current.CommonDirectory], cancellationToken).ConfigureAwait(false);
        using IDisposable operation = await this.locks.EnterAsync(current.CommonDirectory, cancellationToken).ConfigureAwait(false);
        // A named remote is required; an arbitrary URL/path is not silently treated as one. This operation copies no
        // remote URL of its own into the SDK result or diagnostics; a failure carries only Git's own reason, whose URLs
        // GitOperationChecks.Reason strips of their user name and password.
        Check(await this.git.RunAsync(current.WorktreePath, ["remote", "get-url", "--", request.RemoteName],
            cancellationToken).ConfigureAwait(false), "resolve configured fetch remote");
        foreach (string reference in refs)
            foreach (string part in reference.Split(':'))
                Check(await this.git.RunAsync(current.WorktreePath, ["check-ref-format", "--allow-onelevel", part],
                    cancellationToken).ConfigureAwait(false), "validate fetch reference");
        // Source-only refs update no ref (--refmap=), so only mapping destinations can change.
        string[] destinations = [.. refs.Select(reference => reference.Split(':')).Where(parts => parts.Length == 2)
            .Select(parts => parts[1]).Distinct(StringComparer.Ordinal)];
        IReadOnlyDictionary<string, string> before = await this.ReadDestinationsAsync(current.WorktreePath, destinations, cancellationToken)
            .ConfigureAwait(false);
        GitRunResult result = await this.git.RunAsync(current.WorktreePath,
            ["fetch", "--atomic", "--no-all", "--no-prune", "--no-prune-tags", "--no-tags",
                "--no-recurse-submodules", "--no-auto-maintenance", "--no-write-commit-graph", "--refmap=", "--", request.RemoteName, .. refs],
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> after = await this.ReadDestinationsAsync(current.WorktreePath, destinations, cancellationToken)
            .ConfigureAwait(false);
        List<GitRefUpdate> updates = [];
        foreach (string destination in destinations)
        {
            string? previous = before.GetValueOrDefault(destination);
            string? now = after.GetValueOrDefault(destination);
            if (!string.Equals(previous, now, StringComparison.Ordinal)) updates.Add(new(destination, previous, now));
        }
        ReadOnlyCollection<GitRefUpdate> updated = updates.AsReadOnly();
        if (result.Exit.ExitCode == 0 && result.Exit.Diagnostic is null) return new(request.RemoteName, updated);
        // --atomic: when Git refuses one ref, it updates none of them, unless another process changed a ref meanwhile.
        // Git's own reason follows the sentence, so a refused key or an unverified host key names itself instead of
        // leaving the caller with nothing but the failure. Git writes none for a ref it merely refuses to fast-forward.
        return new(request.RemoteName, updated, (updated.Count == 0
            ? "Fetch did not complete successfully; inspect repository state before another action. No retry or cleanup was attempted."
            : "Fetch did not complete successfully after changing refs; inspect repository state before another action. No retry or cleanup was attempted.")
            + (Reason(result.Errors) is { } reason ? " " + reason : string.Empty));
    }

    // The commit each destination names now, read with for-each-ref. A pattern also matches refs below it, so only a line
    // whose ref equals a destination counts. A destination absent from the result names no commit.
    private async Task<IReadOnlyDictionary<string, string>> ReadDestinationsAsync(string worktree, string[] destinations,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> commits = new(StringComparer.Ordinal);
        if (destinations.Length == 0) return commits;
        GitRunResult result = await this.git.RunReadAsync(worktree,
            ["for-each-ref", "--format=%(objectname) %(refname)", "--", .. destinations], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Check(result, "read fetch destinations");
        string text = new UTF8Encoding(false, true).GetString(result.Output);
        if (text.Length != 0 && !text.EndsWith('\n')) throw new InvalidDataException("Git returned incomplete fetch evidence.");
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0 || !ObjectId(line[..space])) throw new InvalidDataException("Git returned invalid fetch identities.");
            string name = line[(space + 1)..];
            if (destinations.Contains(name, StringComparer.Ordinal)) commits[name] = line[..space];
        }
        return commits;
    }

    private static bool ObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
}
