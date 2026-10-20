using System.Globalization;
using System.Text;

namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    /// <summary>
    /// One page of the commits that changed one file, newest first, following renames, with the file's path at each commit.
    /// The revision resolves once. <c>log --follow</c> cannot skip commits and still follow renames, so the walk reads
    /// <see cref="GitFileHistoryRequest.Skip"/> + <see cref="GitFileHistoryRequest.Limit"/> + 1 commits from the start and
    /// parses the skipped ones too: their renames decide the path of later commits. The extra commit decides
    /// <see cref="GitFileHistoryPage.HasMore"/>. Each path at a commit is checked through the path policy; the history ends
    /// before the first path the policy refuses.
    /// </summary>
    internal async Task<GitFileHistoryPage> ReadFileHistoryAsync(GitFileHistoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Skip);
        if (request.Path is null || !GitPaths.IsRelativePath(request.Path))
            throw new ArgumentException("File history needs a literal repository-relative path.", nameof(request));
        GitLayout layout = await this.OpenAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        await this.RequireReadableAsync(GitPaths.Combine(worktree, request.Path), cancellationToken).ConfigureAwait(false);
        string resolved = await this.ResolveCommitAsync(worktree, request.Revision, cancellationToken).ConfigureAwait(false);
        long count = (long)request.Skip + request.Limit + 1;
        byte[] output = await this.ReadAsync(worktree,
            ["log", "--follow", "--find-renames", "--name-status", "-z", "--no-color", "--no-decorate", "--no-show-signature",
                "--format=%x00%H%x00%P%x00%an%x00%aI%x00%s", "--max-count=" + count.ToString(CultureInfo.InvariantCulture),
                "--end-of-options", resolved, "--", request.Path], cancellationToken).ConfigureAwait(false);
        List<GitFileVersion> versions = [];
        string path = request.Path;
        foreach ((GitCommitSummary commit, string? atCommit, string? before) in ParseFileHistory(output, layout.ObjectFormat))
        {
            string current = atCommit ?? path;
            if (!await PermitsAsync(this.paths, GitPaths.Combine(worktree, current), cancellationToken).ConfigureAwait(false)) break;
            versions.Add(new(commit, current));
            path = before ?? current;
        }
        bool more = versions.Count > request.Skip + request.Limit;
        return new(resolved, versions.Skip(request.Skip).Take(request.Limit).ToList().AsReadOnly(), more);
    }

    /// <summary>
    /// Parses <c>log --name-status -z</c> with the format <c>%x00%H%x00%P%x00%an%x00%aI%x00%s</c>. Each commit is an empty
    /// field and five fields; a status field that follows starts with a newline, then holds the status letters, and is
    /// followed by one path, or by the old and the new path for a rename or copy. A commit without a status, such as a
    /// merge, reports no path.
    /// </summary>
    private static List<(GitCommitSummary Commit, string? Path, string? PreviousPath)> ParseFileHistory(byte[] output,
        GitObjectFormat format)
    {
        List<(int Offset, int Length)> fields = [];
        int offset = 0;
        while (offset < output.Length)
        {
            int end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Git returned an unterminated history field.");
            fields.Add((offset, end - offset));
            offset = end + 1;
        }
        List<(GitCommitSummary, string?, string?)> commits = [];
        int index = 0;
        while (index < fields.Count)
        {
            if (fields[index].Length != 0 || index + 5 >= fields.Count)
                throw new InvalidDataException("Git returned an incomplete history record.");
            string[] record = new string[5];
            for (int field = 0; field < 5; field++)
            {
                (int start, int length) = fields[index + 1 + field];
                // Names and subjects come from other people's commits: bytes that are not UTF-8 become replacement characters.
                record[field] = Encoding.UTF8.GetString(output, start, length);
            }
            index += 6;
            string? path = null;
            string? previous = null;
            while (index < fields.Count && fields[index].Length > 0 && output[fields[index].Offset] == (byte)'\n')
            {
                string status = Encoding.ASCII.GetString(output, fields[index].Offset + 1, fields[index].Length - 1);
                if (status.Length == 0)
                {
                    index++;
                    continue;
                }
                bool moved = status.StartsWith('R') || status.StartsWith('C');
                int paths = moved ? 2 : 1;
                if (index + paths >= fields.Count) throw new InvalidDataException("Git returned an invalid history status.");
                path = PathField(output, fields[index + paths]);
                previous = moved && status[0] == 'R' ? PathField(output, fields[index + 1]) : null;
                index += paths + 1;
            }
            commits.Add((Summary(record, format), path, previous));
        }
        return commits;
    }

    private static string PathField(byte[] output, (int Offset, int Length) field) =>
        GitPaths.Decode(output.AsSpan(field.Offset, field.Length));

    // ID, parents, author name, strict ISO 8601 author time and subject: one history record of either history read.
    private static GitCommitSummary Summary(string[] record, GitObjectFormat format)
    {
        string[] parents = record[1].Length == 0 ? [] : record[1].Split(' ');
        if (!GitPaths.IsObjectId(record[0], format) || parents.Any(parent => !GitPaths.IsObjectId(parent, format)))
            throw new InvalidDataException("Git returned an invalid commit identity.");
        if (!DateTimeOffset.TryParseExact(record[3], "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset authored))
            throw new InvalidDataException("Git returned an invalid author time.");
        return new(record[0], Array.AsReadOnly(parents), record[2], authored, record[4]);
    }
}
