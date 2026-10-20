using System.Globalization;
using System.Text;

namespace Darkspyre.Common.Git;

internal sealed partial class GitRepositoryReader
{
    /// <summary>
    /// Reads one committed file into memory: the directory and then the path are checked through the path policy, the
    /// reference resolves once, the literal tree entry is listed without its size, and the blob is read through one
    /// <see cref="GitBlobReader"/> that is disposed before the call returns. The reader checks the byte limit from the
    /// object header before it reads the body.
    /// </summary>
    internal async Task<GitFileContent> ReadFileAtCommitAsync(GitFileAtCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path is null || !GitPaths.IsRelativePath(request.Path) || request.MaximumBytes is < 0 || request.MaximumBytes > Array.MaxLength)
            throw new ArgumentException("A committed file needs a literal repository-relative path and a byte limit from 0 to Array.MaxLength.",
                nameof(request));
        GitLayout layout = await this.OpenAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        // A path the policy refuses is refused before Git is asked about it, even when no file exists there now.
        await this.RequireReadableAsync(GitPaths.Combine(worktree, request.Path), cancellationToken).ConfigureAwait(false);
        string commit = await this.ResolveCommitAsync(worktree, request.Reference, cancellationToken).ConfigureAwait(false);
        // Without -l, Git reads no blob for its size.
        byte[] listing = await this.ReadAsync(worktree, ["ls-tree", "-z", "--full-tree", commit, "--", request.Path],
            cancellationToken).ConfigureAwait(false);
        if (listing.Length == 0) return new(commit, false, null, null);
        List<TreeRecord> records = ParseTree(listing, layout.ObjectFormat, withSize: false);
        if (records is not [var record] || record.Path != request.Path)
            throw new InvalidDataException("Git returned an entry for a different path.");
        if (record.Type is "tree" or "commit")
            throw new GitReadException(GitReadFailure.NotBlob, "The committed path names a directory or a submodule, not a file.");
        await using GitBlobReader reader = await GitBlobReader.StartAsync(this.git, worktree, layout.ObjectFormat, cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = await reader.ReadAsync(record.ObjectId, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
        return new(commit, true, record.Mode, new MemoryStream(bytes, writable: false));
    }

    /// <summary>
    /// Reads one path's stage-zero index entry into memory: the directory and then the path are checked through the path
    /// policy, <c>ls-files --stage -z</c> lists the path's index entries, and the blob is read through one
    /// <see cref="GitBlobReader"/> that checks the byte limit from the object header before it reads the body.
    /// </summary>
    internal async Task<GitIndexFileContent> ReadFileAtIndexAsync(GitIndexFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path is null || !GitPaths.IsRelativePath(request.Path) || request.MaximumBytes is < 0 || request.MaximumBytes > Array.MaxLength)
            throw new ArgumentException("An index read needs a literal repository-relative path and a byte limit from 0 to Array.MaxLength.",
                nameof(request));
        GitLayout layout = await this.OpenAsync(request.Repository, cancellationToken).ConfigureAwait(false);
        string worktree = layout.WorktreePath;
        await this.RequireReadableAsync(GitPaths.Combine(worktree, request.Path), cancellationToken).ConfigureAwait(false);
        byte[] listing = await this.ReadAsync(worktree, ["ls-files", "--stage", "-z", "--", request.Path], cancellationToken)
            .ConfigureAwait(false);
        // A pathspec naming a folder lists the files below it; only the exact path is the file's entry.
        List<IndexRecord> records = [.. ParseIndex(listing, layout.ObjectFormat).Where(record => record.Path == request.Path)];
        if (records.Count == 0) return new(false, null, null);
        if (records.FirstOrDefault(record => record.Stage == '0') is not { } merged)
            throw new GitOperationException(GitFailure.NotClean, "The path has unresolved conflicts and no merged index entry; nothing was read.");
        if (merged.Mode == "160000")
            throw new GitReadException(GitReadFailure.NotBlob, "The index entry is a submodule, not a file.");
        await using GitBlobReader reader = await GitBlobReader.StartAsync(this.git, worktree, layout.ObjectFormat, cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = await reader.ReadAsync(merged.ObjectId, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
        return new(true, merged.Mode, new MemoryStream(bytes, writable: false));
    }

    /// <summary>One <c>ls-files --stage -z</c> record: mode, object ID, stage digit and path.</summary>
    private sealed record IndexRecord(string Mode, string ObjectId, char Stage, string Path);

    // NUL-terminated records "<mode> <oid> <stage>\t<path>"; the path's bytes are decoded exactly.
    private static List<IndexRecord> ParseIndex(byte[] output, GitObjectFormat format)
    {
        List<IndexRecord> records = [];
        int offset = 0;
        while (offset < output.Length)
        {
            int end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Git returned an unterminated index entry.");
            int tab = Array.IndexOf(output, (byte)'\t', offset, end - offset);
            if (tab < 0) throw new InvalidDataException("Git returned an invalid index entry.");
            string[] fields = Encoding.ASCII.GetString(output, offset, tab - offset).Split(' ');
            if (fields is not [var mode, var objectId, [var stage]] || !GitPaths.IsObjectId(objectId, format) || stage is < '0' or > '3')
                throw new InvalidDataException("Git returned an invalid index entry.");
            records.Add(new(mode, objectId, stage, GitPaths.Decode(output.AsSpan(tab + 1, end - tab - 1))));
            offset = end + 1;
        }
        return records;
    }

    /// <summary>
    /// The files, links and submodules of one commit under a folder, in Git's byte order of paths, leaving out every entry
    /// the path policy does not let the caller read. The listing is held in memory.
    /// </summary>
    internal async Task<IReadOnlyList<GitTreeEntry>> ReadTreeAsync(GitTreeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Folder);
        if (request.Folder.Length != 0 && !GitPaths.IsRelativePath(request.Folder))
            throw new ArgumentException("A tree folder must be empty or a literal repository-relative path.", nameof(request));
        GitLayout layout = await this.OpenAsync(request.RepositoryPath, cancellationToken).ConfigureAwait(false);
        // The commit is passed to Git as an argument, so it must be an object ID, never an option or a revision expression.
        if (request.CommitId is null || !GitPaths.IsObjectId(request.CommitId, layout.ObjectFormat))
            throw new ArgumentException("A tree read needs the full commit ID of the repository's format.", nameof(request));
        string worktree = layout.WorktreePath;
        string[] folder = request.Folder.Length == 0 ? [] : ["--", request.Folder];
        byte[] listing = await this.ReadAsync(worktree, ["ls-tree", "-r", "-l", "-z", "--full-tree", request.CommitId, .. folder],
            cancellationToken).ConfigureAwait(false);
        List<GitTreeEntry> entries = [];
        foreach (TreeRecord record in ParseTree(listing, layout.ObjectFormat, withSize: true))
        {
            GitEntryKind kind = record.Mode switch
            {
                "100644" => GitEntryKind.RegularFile,
                "100755" => GitEntryKind.ExecutableFile,
                "120000" => GitEntryKind.SymbolicLink,
                "160000" => GitEntryKind.GitLink,
                _ => throw new InvalidDataException("Git returned a tree entry with an unexpected mode."),
            };
            if (kind != GitEntryKind.GitLink && record.Length is null) throw new InvalidDataException("Git returned a blob without its size.");
            if (await PermitsAsync(this.paths, GitPaths.Combine(worktree, record.Path), cancellationToken).ConfigureAwait(false))
                entries.Add(new(record.Path, kind, record.Mode, record.ObjectId, kind == GitEntryKind.GitLink ? null : record.Length));
        }
        return entries.AsReadOnly();
    }

    /// <summary>
    /// Authorizes the caller's directory, locates its worktree and starts one <see cref="GitBlobReader"/> there. The caller
    /// disposes it before its call ends.
    /// </summary>
    internal async Task<GitBlobReader> OpenBlobReaderAsync(string directory, CancellationToken cancellationToken)
    {
        GitLayout layout = await this.OpenAsync(directory, cancellationToken).ConfigureAwait(false);
        return await GitBlobReader.StartAsync(this.git, layout.WorktreePath, layout.ObjectFormat, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One <c>ls-tree -z</c> record: mode, type, object ID, size (null for <c>-</c>, and for a listing without
    /// <c>-l</c>) and path.
    /// </summary>
    private sealed record TreeRecord(string Mode, string Type, string ObjectId, long? Length, string Path);

    // NUL-terminated records "<mode> <type> <oid> <padded size>\t<path>" with -l (withSize) and
    // "<mode> <type> <oid>\t<path>" without it; the path's bytes are decoded exactly.
    private static List<TreeRecord> ParseTree(byte[] output, GitObjectFormat format, bool withSize)
    {
        List<TreeRecord> records = [];
        int offset = 0;
        while (offset < output.Length)
        {
            int end = Array.IndexOf(output, (byte)0, offset);
            if (end < 0) throw new InvalidDataException("Git returned an unterminated tree entry.");
            int tab = Array.IndexOf(output, (byte)'\t', offset, end - offset);
            if (tab < 0) throw new InvalidDataException("Git returned an invalid tree entry.");
            string[] fields = Encoding.ASCII.GetString(output, offset, tab - offset).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long? length = null;
            if (withSize)
            {
                if (fields is not [_, _, _, var size]) throw new InvalidDataException("Git returned an invalid tree entry.");
                length = size == "-" ? null
                    : long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed
                    : throw new InvalidDataException("Git returned a tree entry with a size that is not a number.");
            }
            else if (fields.Length != 3) throw new InvalidDataException("Git returned an invalid tree entry.");
            if (!GitPaths.IsObjectId(fields[2], format)) throw new InvalidDataException("Git returned an invalid tree entry.");
            records.Add(new(fields[0], fields[1], fields[2], length, GitPaths.Decode(output.AsSpan(tab + 1, end - tab - 1))));
            offset = end + 1;
        }
        return records;
    }
}
