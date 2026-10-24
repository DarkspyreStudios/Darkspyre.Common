using System.Globalization;
using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>
/// A working tree as one status read observed it, before the caller's scope filters any path: the host's own view for
/// clean checks and sequence classification. It never leaves the host.
/// </summary>
/// <param name="CommonDirectory">The real Git common directory.</param>
/// <param name="Worktree">The real worktree root.</param>
/// <param name="ObjectFormat">The repository's object format.</param>
/// <param name="Head">The commit HEAD names, or null when the branch has no commits.</param>
/// <param name="BranchRef">The checked-out branch's full ref, or null when HEAD is detached.</param>
/// <param name="UpstreamName">The upstream status names, or null when there is none.</param>
/// <param name="Ahead">The ahead count, or null when the upstream is gone or absent.</param>
/// <param name="Behind">The behind count, or null when the upstream is gone or absent.</param>
/// <param name="Changes">Every changed and untracked path.</param>
/// <param name="ConflictedPaths">Every unmerged path.</param>
/// <param name="OperationKind">The kind of the operation in progress, or null.</param>
/// <param name="OperationPaths">The six per-worktree paths whose existence decides the operation.</param>
/// <param name="ObservedAt">When the status command returned.</param>
internal sealed record GitWorkingState(string CommonDirectory, string Worktree, GitObjectFormat ObjectFormat, string? Head,
    string? BranchRef, string? UpstreamName, int? Ahead, int? Behind, IReadOnlyList<GitChange> Changes,
    IReadOnlyList<string> ConflictedPaths, string? OperationKind, GitOperationPaths OperationPaths, DateTimeOffset ObservedAt);

/// <summary>
/// The per-worktree paths <c>rev-parse --git-path</c> names for <c>rebase-merge</c>, <c>rebase-apply</c>,
/// <c>MERGE_HEAD</c>, <c>CHERRY_PICK_HEAD</c>, <c>REVERT_HEAD</c> and <c>sequencer</c>, in that order.
/// </summary>
internal sealed record GitOperationPaths(string RebaseMerge, string RebaseApply, string MergeHead, string CherryPickHead,
    string RevertHead, string Sequencer);

internal sealed partial class GitRepositoryReader
{
    /// <summary>
    /// The working tree's state for the caller: HEAD, the branch, its upstream from local refs, the changes and conflicted
    /// paths the path policy lets the caller read and the operation in progress. Null when the directory is not inside a working
    /// tree; a bare repository throws <see cref="NotSupportedException"/>.
    /// </summary>
    internal async Task<GitStatusSnapshot?> ReadStatusAsync(string repository, CancellationToken cancellationToken)
    {
        GitWorkingState? state = await this.ReadWorkingStateAsync(repository, cancellationToken).ConfigureAwait(false);
        if (state is null) return null;
        GitUpstream? upstream = await this.ReadUpstreamAsync(state, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> conflicted = await this.PermittedAsync(state.Worktree, state.ConflictedPaths, cancellationToken)
            .ConfigureAwait(false);
        List<GitChange> changes = [];
        foreach (GitChange change in state.Changes)
        {
            // A rename is reported only when both of its paths are readable, so a refused name never appears.
            IReadOnlyList<string> paths = change.OriginalPath is null ? [change.Path] : [change.Path, change.OriginalPath];
            if ((await this.PermittedAsync(state.Worktree, paths, cancellationToken).ConfigureAwait(false)).Count == paths.Count)
                changes.Add(change);
        }
        GitOperationState? operation = state.OperationKind is { } kind ? new(kind, conflicted) : null;
        return new(state.CommonDirectory, state.Worktree, state.Head, state.BranchRef, upstream, changes.AsReadOnly(), operation,
            state.ObservedAt);
    }

    /// <summary>
    /// The working tree's unfiltered state: the shared layout reader, then <c>status --porcelain=v2 --branch -z
    /// --untracked-files=all</c> from the worktree root, then the operation the six per-worktree Git paths show. Null when
    /// the directory is not inside a working tree.
    /// </summary>
    internal async Task<GitWorkingState?> ReadWorkingStateAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout? layout = await this.LocateAsync(directory, cancellationToken).ConfigureAwait(false);
        if (layout is null) return null;
        byte[] output = await this.ReadAsync(layout.WorktreePath,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        PorcelainStatus status = ParsePorcelainStatus(output, layout.ObjectFormat);
        GitOperationPaths paths = await this.ReadOperationPathsAsync(layout.WorktreePath, cancellationToken).ConfigureAwait(false);
        return new(layout.CommonDirectory, layout.WorktreePath, layout.ObjectFormat, status.Head, status.BranchRef, status.Upstream,
            status.Ahead, status.Behind, status.Changes.AsReadOnly(), status.Conflicted.AsReadOnly(), OperationKind(paths), paths, observedAt);
    }

    // The upstream's full ref comes from for-each-ref; its commit resolves once, and only while status still counts it.
    private async Task<GitUpstream?> ReadUpstreamAsync(GitWorkingState state, CancellationToken cancellationToken)
    {
        if (state.UpstreamName is null || state.BranchRef is null) return null;
        byte[] output = await this.ReadAsync(state.Worktree, ["for-each-ref", "--format=%(upstream)", state.BranchRef],
            cancellationToken).ConfigureAwait(false);
        string[] lines = Utf8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        if (lines.Length != 1 || !lines[0].StartsWith("refs/", StringComparison.Ordinal))
            throw new InvalidDataException("Git returned an invalid upstream ref.");
        // Without branch.ab, the upstream ref no longer exists locally ("gone").
        if (state.Ahead is null) return new(lines[0], null, null, null);
        string? commit = await this.TryResolveCommitAsync(state.Worktree, lines[0], cancellationToken).ConfigureAwait(false);
        return commit is not null ? new(lines[0], commit, state.Ahead, state.Behind) : new(lines[0], null, null, null);
    }

    private async Task<GitOperationPaths> ReadOperationPathsAsync(string worktree, CancellationToken cancellationToken)
    {
        byte[] output = await this.ReadAsync(worktree,
            ["rev-parse", "--path-format=absolute", "--git-path", "rebase-merge", "--git-path", "rebase-apply", "--git-path", "MERGE_HEAD",
                "--git-path", "CHERRY_PICK_HEAD", "--git-path", "REVERT_HEAD", "--git-path", "sequencer"], cancellationToken).ConfigureAwait(false);
        string text = Line(output);
        string[] paths = text.Split('\n');
        if (paths.Length != 6 || paths.Any(path => !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Git returned invalid operation paths.");
        return new(paths[0], paths[1], paths[2], paths[3], paths[4], paths[5]);
    }

    // The first match decides. A sequencer without either head file is the cherry-pick or revert its todo names first,
    // as Git's own status decides; anything else in it is no operation.
    private static string? OperationKind(GitOperationPaths paths)
    {
        if (Directory.Exists(paths.RebaseMerge)) return "rebase";
        if (Directory.Exists(paths.RebaseApply)) return File.Exists(Path.Combine(paths.RebaseApply, "applying")) ? "am" : "rebase";
        if (File.Exists(paths.MergeHead)) return "merge";
        if (File.Exists(paths.CherryPickHead)) return "cherry-pick";
        if (File.Exists(paths.RevertHead)) return "revert";
        string todo = Path.Combine(paths.Sequencer, "todo");
        if (!Directory.Exists(paths.Sequencer) || !File.Exists(todo)) return null;
        string first;
        using (StreamReader reader = new(todo, Utf8)) first = reader.ReadLine() ?? "";
        return first.Split(' ', 2)[0] switch
        {
            "pick" => "cherry-pick",
            "revert" => "revert",
            _ => null,
        };
    }

    /// <summary>
    /// Parses <c>status --porcelain=v2 --branch -z</c>. Headers give HEAD, the branch, the upstream and its counts; a header
    /// this parser does not know is skipped, as Git documents. Records are <c>1</c>, <c>2</c> with its original path in the
    /// next field, <c>u</c>, which is also a conflicted path, and <c>?</c>. A path is the rest of its record after the fixed
    /// fields, so it may contain spaces. Git ends an untracked nested repository's path with <c>/</c>; it is one untracked
    /// change named without the slash. Any other record is <see cref="InvalidDataException"/>.
    /// </summary>
    private static PorcelainStatus ParsePorcelainStatus(byte[] output, GitObjectFormat format)
    {
        PorcelainStatus status = new();
        int offset = 0;
        while (offset < output.Length)
        {
            string record = StatusField(output, ref offset);
            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                ReadHeader(status, record[2..], format);
                continue;
            }
            switch (record.Length > 1 && record[1] == ' ' ? record[0] : '\0')
            {
                case '1':
                {
                    string[] fields = record.Split(' ', 9);
                    if (fields.Length != 9 || fields[1].Length != 2) throw InvalidStatus();
                    status.Changes.Add(new(RequirePath(fields[8]), null, fields[1][0], fields[1][1]));
                    break;
                }
                case '2':
                {
                    string[] fields = record.Split(' ', 10);
                    if (fields.Length != 10 || fields[1].Length != 2) throw InvalidStatus();
                    string original = RequirePath(StatusField(output, ref offset));
                    status.Changes.Add(new(RequirePath(fields[9]), original, fields[1][0], fields[1][1]));
                    break;
                }
                case 'u':
                {
                    string[] fields = record.Split(' ', 11);
                    if (fields.Length != 11 || fields[1].Length != 2) throw InvalidStatus();
                    string path = RequirePath(fields[10]);
                    status.Changes.Add(new(path, null, fields[1][0], fields[1][1]));
                    status.Conflicted.Add(path);
                    break;
                }
                case '?':
                    status.Changes.Add(new(RequirePath(record.EndsWith('/') ? record[2..^1] : record[2..]), null, '?', '?'));
                    break;
                default:
                    throw InvalidStatus();
            }
        }
        return status;
    }

    private static void ReadHeader(PorcelainStatus status, string header, GitObjectFormat format)
    {
        int space = header.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0) return;
        string value = header[(space + 1)..];
        switch (header[..space])
        {
            case "branch.oid":
                if (value == "(initial)") status.Head = null;
                else if (GitPaths.IsObjectId(value, format)) status.Head = value;
                else throw InvalidStatus();
                break;
            case "branch.head":
                status.BranchRef = value == "(detached)" ? null : "refs/heads/" + value;
                break;
            case "branch.upstream":
                status.Upstream = value;
                break;
            case "branch.ab":
                string[] counts = value.Split(' ');
                if (counts.Length != 2 || !counts[0].StartsWith('+') || !counts[1].StartsWith('-')
                    || !int.TryParse(counts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int ahead)
                    || !int.TryParse(counts[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int behind))
                    throw InvalidStatus();
                (status.Ahead, status.Behind) = (ahead, behind);
                break;
        }
    }

    private static string StatusField(byte[] output, ref int offset)
    {
        int end = Array.IndexOf(output, (byte)0, offset);
        if (end < 0) throw new InvalidDataException("Git returned an unterminated status record.");
        string text;
        try { text = Utf8.GetString(output, offset, end - offset); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Git returned a status path that is not UTF-8."); }
        offset = end + 1;
        return text;
    }

    private static string RequirePath(string path) => GitPaths.IsRelativePath(path) ? path : throw InvalidStatus();

    private static InvalidDataException InvalidStatus() => new("Git returned an invalid status record.");

    private sealed class PorcelainStatus
    {
        internal string? Head { get; set; }
        internal string? BranchRef { get; set; }
        internal string? Upstream { get; set; }
        internal int? Ahead { get; set; }
        internal int? Behind { get; set; }
        internal List<GitChange> Changes { get; } = [];
        internal List<string> Conflicted { get; } = [];
    }
}
