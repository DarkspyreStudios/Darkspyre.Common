using System.Globalization;
using System.Text;
using static Darkspyre.Common.Git.GitOperationChecks;

namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    /// <summary>
    /// One page of commit summaries from <see cref="GitHistoryRequest.Revision"/>, resolved once, newest first. Each path
    /// filter is checked through the path policy before the history is read. The walk ignores <c>log.follow</c> and
    /// signature configuration, and runs from the worktree root, so filters are repository-relative.
    /// </summary>
    internal async Task<GitHistoryPage> ReadHistoryAsync(GitHistoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Skip);
        string[] filters = [.. request.Paths];
        if (filters.Any(path => !GitPaths.IsRelativePath(path)))
            throw new ArgumentException("History paths must be literal repository-relative paths.", nameof(request));
        GitLayout layout = await this.OpenAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        await this.AuthorizeFiltersAsync(layout.WorktreePath, filters, cancellationToken).ConfigureAwait(false);
        string resolved = await this.ResolveCommitAsync(layout.WorktreePath, request.Revision, cancellationToken).ConfigureAwait(false);
        // One commit past the limit says whether another page follows.
        long count = (long)request.Limit + 1;
        byte[] output = await this.ReadAsync(layout.WorktreePath,
            ["log", "-z", "--no-color", "--no-decorate", "--no-show-signature", "--no-follow", "--format=%H%x00%P%x00%an%x00%aI%x00%s",
                "--skip=" + request.Skip.ToString(CultureInfo.InvariantCulture), "--max-count=" + count.ToString(CultureInfo.InvariantCulture),
                "--end-of-options", resolved, "--", .. filters], cancellationToken).ConfigureAwait(false);
        List<GitCommitSummary> commits = ParseHistory(output, layout.ObjectFormat);
        bool more = commits.Count > request.Limit;
        if (more) commits.RemoveAt(commits.Count - 1);
        return new(resolved, commits.AsReadOnly(), more);
    }

    /// <summary>
    /// Resolves both revisions once and reads every merge base and the left and right counts of the symmetric difference.
    /// </summary>
    internal async Task<GitAncestryResult> ReadAncestryAsync(GitAncestryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        GitLayout layout = await this.OpenAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        string left = await this.ResolveCommitAsync(worktree, request.Left, cancellationToken).ConfigureAwait(false);
        string right = await this.ResolveCommitAsync(worktree, request.Right, cancellationToken).ConfigureAwait(false);
        GitRunResult bases = await this.RunAsync(worktree, ["merge-base", "--all", left, right], cancellationToken).ConfigureAwait(false);
        // Exit 1 with no output is Git's answer for histories with no common ancestor.
        string[] mergeBases = bases.Exit is { ExitCode: 1, Diagnostic: null } && bases.Output.Length == 0 ? []
            : ObjectIdLines(Checked(bases, "read merge bases"), layout.ObjectFormat);
        string counts = Line(await this.ReadAsync(worktree, ["rev-list", "--left-right", "--count", left + "..." + right],
            cancellationToken).ConfigureAwait(false));
        string[] sides = counts.Split('\t');
        if (sides.Length != 2 || !int.TryParse(sides[0], NumberStyles.None, CultureInfo.InvariantCulture, out int leftAhead)
            || !int.TryParse(sides[1], NumberStyles.None, CultureInfo.InvariantCulture, out int rightAhead))
            throw new InvalidDataException("Git returned invalid ancestry counts.");
        return new(left, right, Array.AsReadOnly(mergeBases), leftAhead, rightAhead, rightAhead == 0, leftAhead == 0);
    }

    /// <summary>
    /// The paths of <paramref name="paths"/> the path policy lets the caller read, in order. A refused path is left out
    /// without a trace.
    /// </summary>
    internal async Task<IReadOnlyList<string>> PermittedAsync(string worktree, IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        List<string> permitted = [];
        foreach (string path in paths)
            if (await PermitsAsync(this.paths, GitPaths.Combine(worktree, path), cancellationToken).ConfigureAwait(false))
                permitted.Add(path);
        return permitted.AsReadOnly();
    }

    // A filter the path policy refuses is refused before Git reads anything through it, with
    // UnauthorizedAccessException.
    private async Task AuthorizeFiltersAsync(string worktree, string[] filters, CancellationToken cancellationToken)
    {
        foreach (string path in filters)
            await this.RequireReadableAsync(GitPaths.Combine(worktree, path), cancellationToken).ConfigureAwait(false);
    }

    // Groups of five NUL-terminated fields: ID, parents, author name, strict ISO 8601 author time, subject.
    private static List<GitCommitSummary> ParseHistory(byte[] output, GitObjectFormat format)
    {
        List<GitCommitSummary> commits = [];
        List<string> fields = [];
        int offset = 0;
        while (offset < output.Length)
        {
            int end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Git returned an unterminated history field.");
            // Names and subjects come from other people's commits: bytes that are not UTF-8 become replacement characters.
            fields.Add(Encoding.UTF8.GetString(output, offset, end - offset));
            offset = end + 1;
        }
        if (fields.Count % 5 != 0) throw new InvalidDataException("Git returned an incomplete history record.");
        for (int index = 0; index < fields.Count; index += 5)
        {
            string id = fields[index];
            string[] parents = fields[index + 1].Length == 0 ? [] : fields[index + 1].Split(' ');
            if (!GitPaths.IsObjectId(id, format) || parents.Any(parent => !GitPaths.IsObjectId(parent, format)))
                throw new InvalidDataException("Git returned an invalid commit identity.");
            if (!DateTimeOffset.TryParseExact(fields[index + 3], "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset authored))
                throw new InvalidDataException("Git returned an invalid author time.");
            commits.Add(new(id, Array.AsReadOnly(parents), fields[index + 2], authored, fields[index + 4]));
        }
        return commits;
    }

    private static byte[] Checked(GitRunResult result, string operation)
    {
        Check(result, operation);
        return result.Output;
    }

    private static string[] ObjectIdLines(byte[] output, GitObjectFormat format)
    {
        string text = Encoding.ASCII.GetString(output);
        if (text.Length != 0 && !text.EndsWith('\n')) throw new InvalidDataException("Git returned an incomplete line.");
        string[] ids = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Any(id => !GitPaths.IsObjectId(id, format))) throw new InvalidDataException("Git returned an invalid commit identity.");
        return ids;
    }
}
