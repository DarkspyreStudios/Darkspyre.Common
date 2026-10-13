using System.Globalization;
using System.Collections.ObjectModel;
using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

/// <summary>
/// Typed read operations on the same Git transport used by repository mutations. A read that takes a directory from a
/// caller checks it, and every path it returns, through the caller's path policy <paramref name="paths"/>.
/// </summary>
internal sealed partial class GitRepositoryReader(GitCommands git, IGitPathPolicy paths)
{
    private readonly GitCommands git = git;
    private readonly IGitPathPolicy paths = paths;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Where a working tree is: its root, common directory, object format and HEAD.</summary>
    internal sealed record GitLayout(string WorktreePath, string CommonDirectory, GitObjectFormat ObjectFormat, string? HeadCommit);

    internal async Task<GitRepositorySnapshot?> InspectAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        GitLayout? layout = await this.LocateAsync(directory, cancellationToken).ConfigureAwait(false);
        if (layout is null) return null;
        GitRunResult branch = await this.RunAsync(layout.WorktreePath, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (branch.Exit.ExitCode != 1) Check(branch, "read branch");
        byte[] status = await this.ReadAsync(layout.WorktreePath,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=traditional"], cancellationToken).ConfigureAwait(false);
        string administrative = this.paths.Resolve(Text(await this.ReadAsync(layout.WorktreePath,
            ["rev-parse", "--absolute-git-dir"], cancellationToken).ConfigureAwait(false)));
        return new(layout.CommonDirectory, layout.WorktreePath, layout.HeadCommit,
            branch.Exit.ExitCode == 0 ? Line(branch.Output) : null, ParseStatus(status), administrative) { ObjectFormat = layout.ObjectFormat };
    }

    /// <summary>
    /// The working tree containing <paramref name="directory"/>, each step run there: whether it is in a work tree; the
    /// object format with the worktree root; the common directory; then HEAD. A path keeps any newline it contains, since
    /// only the object format precedes it on its line. Git's message for a directory outside any repository means null; a
    /// bare repository throws <see cref="NotSupportedException"/>, as does an object format the host does not support.
    /// </summary>
    internal async Task<GitLayout?> LocateAsync(string directory, CancellationToken cancellationToken)
    {
        GitRunResult inside = await this.RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);
        if (inside.Exit is { ExitCode: 128, Diagnostic: null }
            && Utf8.GetString(inside.Errors).StartsWith("fatal: not a git repository", StringComparison.Ordinal)) return null;
        Check(inside, "inspect repository");
        if (Text(inside.Output) != "true") throw new NotSupportedException("This operation requires a Git working tree, not a bare repository.");
        string formatAndRoot = Text(await this.ReadAsync(directory,
            ["rev-parse", "--show-object-format=storage", "--path-format=absolute", "--show-toplevel"], cancellationToken).ConfigureAwait(false));
        int separator = formatAndRoot.IndexOf('\n', StringComparison.Ordinal);
        if (separator <= 0 || separator == formatAndRoot.Length - 1) throw new InvalidDataException("Git returned an incomplete line.");
        GitObjectFormat format = GitPaths.Format(formatAndRoot[..separator])
            ?? throw new NotSupportedException("The repository uses an object format the host does not support.");
        string common = Text(await this.ReadAsync(directory, ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            cancellationToken).ConfigureAwait(false));
        string? commit = await this.TryResolveCommitAsync(directory, "HEAD", cancellationToken).ConfigureAwait(false);
        return new(this.paths.Resolve(formatAndRoot[(separator + 1)..]), this.paths.Resolve(common), format, commit);
    }

    /// <summary>
    /// The commit <paramref name="reference"/> resolves to, or null when it resolves to no commit. Any other Git failure
    /// throws <see cref="IOException"/> naming the exit code only.
    /// </summary>
    internal async Task<string?> TryResolveCommitAsync(string directory, string reference,
        CancellationToken cancellationToken)
    {
        RequireReference(reference);
        GitRunResult result = await this.RunAsync(directory,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"], cancellationToken).ConfigureAwait(false);
        if (result.Exit is { ExitCode: 1, Diagnostic: null }) return null;
        Check(result, "resolve the revision");
        string commit = Line(result.Output);
        return commit.Length is 40 or 64 && commit.All(char.IsAsciiHexDigit) ? commit
            : throw new InvalidDataException("Git returned an invalid commit identity.");
    }

    /// <summary>
    /// The commit <paramref name="reference"/> resolves to. A reference that resolves to no commit throws
    /// <see cref="GitOperationException"/> with <see cref="GitFailure.RevisionNotFound"/>.
    /// </summary>
    internal async Task<string> ResolveCommitAsync(string directory, string reference,
        CancellationToken cancellationToken = default) =>
        await this.TryResolveCommitAsync(directory, reference, cancellationToken).ConfigureAwait(false)
            ?? throw new GitOperationException(GitFailure.RevisionNotFound, "The revision does not resolve to a commit; nothing changed.");

    /// <summary>Resolves each of <paramref name="references"/> once, in order; see <see cref="ResolveCommitAsync"/>.</summary>
    internal async Task<IReadOnlyList<string>> ResolveCommitsAsync(string directory,
        IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        string[] commits = new string[references.Count];
        for (int index = 0; index < commits.Length; index++)
            commits[index] = await this.ResolveCommitAsync(directory, references[index], cancellationToken).ConfigureAwait(false);
        return commits;
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> once in the caller's authorized directory. A reference that resolves to no
    /// commit is <see cref="GitCommitResolutionStatus.UnresolvedReference"/>.
    /// </summary>
    internal async Task<GitCommitResolution> ResolveExactCommitAsync(string directory, string reference,
        CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        string? commit = await this.TryResolveCommitAsync(layout.WorktreePath, reference, cancellationToken).ConfigureAwait(false);
        return commit is null ? new(GitCommitResolutionStatus.UnresolvedReference, layout.ObjectFormat, null)
            : new(GitCommitResolutionStatus.Resolved, layout.ObjectFormat, commit);
    }

    /// <summary>
    /// Whether the path policy permits reading <paramref name="absolutePath"/>: it refuses or not. A path with no file now
    /// is still readable, so a historical path under a grant reads. Other failures propagate.
    /// </summary>
    internal static async Task<bool> PermitsAsync(IGitPathPolicy paths, string absolutePath, CancellationToken cancellationToken) =>
        await paths.GetReadAccessAsync(absolutePath, cancellationToken).ConfigureAwait(false) != GitPathAccess.Refused;

    /// <summary>
    /// Authorizes a caller's directory through the path policy before any Git child starts, then locates its working
    /// tree in the directory the policy resolves it to. A refused directory throws <see cref="UnauthorizedAccessException"/>;
    /// a missing one, one that is not a directory, or one outside a working tree throws
    /// <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    private async Task<GitLayout> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        GitPathAccess access = await this.paths.GetReadAccessAsync(directory, cancellationToken).ConfigureAwait(false);
        if (access == GitPathAccess.Refused) throw new UnauthorizedAccessException("The path policy refuses reading the path.");
        if (access != GitPathAccess.Directory) throw NotReadable();
        return await this.LocateAsync(this.paths.Resolve(directory), cancellationToken).ConfigureAwait(false) ?? throw NotReadable();
    }

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> when the path policy refuses reading <paramref name="absolutePath"/>.</summary>
    private async Task RequireReadableAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (!await PermitsAsync(this.paths, absolutePath, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The path policy refuses reading the path.");
    }

    private static DirectoryNotFoundException NotReadable() => new("The directory is not a readable Git working tree.");

    // A reference is one argument after --end-of-options: nonempty and without NUL, CR or LF.
    private static void RequireReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new ArgumentException("A reference must be nonempty and hold no NUL, CR or LF.", nameof(reference));
    }

    // The output without exactly Git's final terminator; whitespace belonging to a real path or ref is kept.
    private static string Text(byte[] output)
    {
        if (output.Length == 0 || output[^1] != '\n') throw new InvalidDataException("Git returned an incomplete line.");
        try { return Utf8.GetString(output.AsSpan(0, output.Length - 1)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Git returned a line that is not UTF-8."); }
    }

    /// <summary>
    /// Reads a comparison's changed files and patch, limited to the files the path policy lets the caller read. Commits
    /// resolve once. A first command lists every changed path without rename detection, so both sides of a rename are
    /// checked; the patch then runs on the permitted paths alone, so a refused file's name and changes never enter the
    /// result. Both children run from the worktree root with live-only output.
    /// </summary>
    internal async Task<GitDiffResult> ReadDiffAsync(GitDiffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Comparison);
        string[] filters = request.Paths?.ToArray() ?? [];
        if (filters.Any(path => !GitPaths.IsRelativePath(path)))
            throw new ArgumentException("Diff paths must be literal repository-relative paths.", nameof(request));
        GitLayout layout = await this.OpenAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        await this.AuthorizeFiltersAsync(worktree, filters, cancellationToken).ConfigureAwait(false);
        GitComparison comparison = request.Comparison;
        string[] sides;
        switch (comparison)
        {
            case CommitToCommit commits:
                string from = await this.ResolveCommitAsync(worktree, commits.FromRef, cancellationToken).ConfigureAwait(false);
                string to = await this.ResolveCommitAsync(worktree, commits.ToRef, cancellationToken).ConfigureAwait(false);
                comparison = new CommitToCommit(from, to);
                sides = [from, to];
                break;
            case HeadToIndex: sides = ["--cached"]; break;
            case IndexToWorktree: sides = []; break;
            default: throw new ArgumentException("Unknown Git comparison.", nameof(request));
        }
        byte[] names = await this.ReadAsync(worktree,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--name-only", "-z", .. sides, "--", .. filters],
            cancellationToken).ConfigureAwait(false);
        List<string> permitted = [];
        foreach (string path in NulFields(names))
            if (await PermitsAsync(this.paths, GitPaths.Combine(worktree, path), cancellationToken).ConfigureAwait(false))
                permitted.Add(path);
        if (permitted.Count == 0) return new(comparison, [], new MemoryStream([], writable: false));
        // The reader's --literal-pathspecs makes every listed path a literal pathspec.
        byte[] output = await this.ReadAsync(worktree,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--numstat", "-z", "--patch", "--binary", .. sides,
                "--", .. permitted], cancellationToken).ConfigureAwait(false);
        return ParseDiff(comparison, output);
    }

    /// <summary>The names of the repository's configured remotes.</summary>
    internal async Task<IReadOnlyList<string>> ReadRemotesAsync(string repository, CancellationToken cancellationToken) =>
        Utf8.GetString(await this.ReadAsync(repository, ["remote"], cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The commits reachable from <paramref name="commit"/> that no remote-tracking branch reaches.</summary>
    internal async Task<IReadOnlyList<string>> ReadUnpublishedCommitsAsync(string repository, string commit,
        CancellationToken cancellationToken)
    {
        // The commit is an object name, never an option: --end-of-options would make --not and --remotes revisions too.
        if (commit.Length is not (40 or 64) || commit.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("A commit object name is required.", nameof(commit));
        string[] commits = Utf8.GetString(await this.ReadAsync(repository,
            ["rev-list", commit, "--not", "--remotes"], cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (commits.Any(item => item.Length is not (40 or 64) || item.Any(character => !char.IsAsciiHexDigit(character))))
            throw new InvalidDataException("Git returned invalid reachability evidence.");
        return commits;
    }

    // Every reader command is inspection metadata, such as status that lists files outside the caller's view, so none
    // is kept for replay.
    private Task<GitRunResult> RunAsync(string directory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) => this.RunAsync(directory, arguments, null, default, cancellationToken);

    private Task<GitRunResult> RunAsync(string directory, IReadOnlyList<string> arguments,
        TimeSpan? timeout, ReadOnlyMemory<byte> input, CancellationToken cancellationToken) => this.git.RunReadAsync(directory,
            ["--literal-pathspecs", .. arguments], timeout, input, cancellationToken);

    /// <summary>
    /// The standard output of one read-only command run with the reader's literal pathspecs and live-only output. A failed
    /// command throws <see cref="IOException"/> naming no Git output.
    /// </summary>
    internal async Task<byte[]> ReadAsync(string directory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        GitRunResult result = await this.RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        Check(result, "read repository");
        // ls-tree can exit zero after a denied lazy fetch and return incomplete size information.
        if (this.git.ReadPolicy == GitReadPolicy.LocalOnly && result.Errors.Length != 0)
            throw new IOException("Git content is unavailable from the local object store.");
        return result.Output;
    }

    // NUL-terminated fields decoded exactly; a final field without its terminator is malformed.
    private static List<string> NulFields(byte[] output)
    {
        List<string> fields = [];
        int offset = 0;
        while (offset < output.Length)
        {
            int end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Git returned an unterminated field.");
            fields.Add(GitPaths.Decode(output.AsSpan(offset, end - offset)));
            offset = end + 1;
        }
        return fields;
    }

    private static string Line(byte[] output)
    {
        if (output.Length == 0 || output[^1] != '\n') throw new InvalidDataException("Git returned an incomplete line.");
        // Remove exactly Git's terminator, not whitespace belonging to a real path/ref.
        return Utf8.GetString(output.AsSpan(0, output.Length - 1));
    }

    private static ReadOnlyCollection<GitStatusEntry> ParseStatus(byte[] output)
    {
        List<GitStatusEntry> entries = [];
        int offset = 0;
        while (offset < output.Length)
        {
            string record = Field(output, ref offset);
            if (record.Length < 4 || record[2] != ' ') throw new InvalidDataException("Git returned an invalid status record.");
            string? original = record[0] is 'R' or 'C' || record[1] is 'R' or 'C' ? Field(output, ref offset) : null;
            entries.Add(new(record[3..], record[..1], record.Substring(1, 1), original,
                record.StartsWith("??", StringComparison.Ordinal), record.StartsWith("!!", StringComparison.Ordinal)));
        }
        return entries.AsReadOnly();
    }

    private static GitDiffResult ParseDiff(GitComparison comparison, byte[] output)
    {
        List<GitChangedFile> files = [];
        int offset = 0;
        while (offset < output.Length)
        {
            string record = Field(output, ref offset);
            if (record.Length == 0) break; // Git separates numstat's NUL records from the raw patch.
            string[] columns = record.Split('\t', 3);
            if (columns.Length != 3 || !Count(columns[0]) || !Count(columns[1]))
                throw new InvalidDataException("Git returned invalid diff metadata.");
            string? original = columns[2].Length == 0 ? Field(output, ref offset) : null;
            string path = original is null ? columns[2] : Field(output, ref offset);
            if (path.Length == 0 || original?.Length == 0) throw new InvalidDataException("Git returned an empty diff path.");
            files.Add(new(path, original, columns[0] == "-" || columns[1] == "-", original is not null));
        }
        // The patch is returned as bytes, not decoded, recomposed, truncated or mixed with stderr.
        return new(comparison, files.AsReadOnly(), new MemoryStream(output, offset, output.Length - offset, writable: false));
    }

    private static bool Count(string text) => text == "-" || long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string Field(byte[] output, ref int offset)
    {
        int end = Array.IndexOf(output, (byte)0, offset);
        if (end < 0) throw new InvalidDataException("Git returned an unterminated metadata field.");
        string text = Utf8.GetString(output, offset, end - offset);
        offset = end + 1;
        return text;
    }
}
