using System.Globalization;
using System.Text;

namespace Darkspyre.Common.Git;

/// <summary>Reads blobs through one plain <c>git cat-file --batch</c> child. One caller reads one blob at a time.</summary>
/// <remarks>
/// A reader lives only for the call that opened it, so that call's admission covers it and the call's processes stop
/// the child when the call ends. Standard error is drained into nothing from the start, so a full pipe never blocks the
/// child, and its bytes are never read. In a partial clone, each read first checks that the object is in the local
/// store, so Git never fetches a missing one.
/// </remarks>
public sealed class GitBlobReader : IAsyncDisposable
{
    // "<64-digit ID> <type> <decimal size>" is under 100 bytes; a longer line means the framing is lost.
    private const int HeaderLimit = 256;
    private readonly IGitProcess process;
    private readonly GitCommands git;
    private readonly string worktree;
    private readonly bool partialClone;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Task drain;
    private readonly GitObjectFormat format;
    private readonly byte[] buffer = new byte[64 * 1024];
    private int start;
    private int end;
    private bool faulted;
    private int disposed;

    private GitBlobReader(IGitProcess process, GitObjectFormat format, GitCommands git, string worktree, bool partialClone)
    {
        this.process = process;
        this.format = format;
        this.git = git;
        this.worktree = worktree;
        this.partialClone = partialClone;
        this.input = process.StandardInput ?? throw new InvalidOperationException("The object reader needs redirected input.");
        this.output = process.StandardOutput ?? throw new InvalidOperationException("The object reader needs redirected output.");
        Stream errors = process.StandardError ?? throw new InvalidOperationException("The object reader needs redirected output.");
        this.drain = errors.CopyToAsync(Stream.Null);
    }

    /// <summary>
    /// Starts the reader in <paramref name="worktree"/> through the caller's processes, after reading once whether the
    /// repository is a partial clone.
    /// </summary>
    internal static async Task<GitBlobReader> StartAsync(GitCommands git, string worktree, GitObjectFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        bool partialClone = await IsPartialCloneAsync(git, worktree, cancellationToken).ConfigureAwait(false);
        IGitProcess process = await git.StartReadAsync(worktree, ["cat-file", "--batch"], cancellationToken).ConfigureAwait(false);
        try { return new(process, format, git, worktree, partialClone); }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The blob's bytes. A blob longer than <paramref name="maximumBytes"/> throws <see cref="GitReadException"/> with
    /// <see cref="GitReadFailure.LimitExceeded"/> and stops the reader.
    /// </summary>
    /// <remarks>
    /// An object that is not in the local object store throws <see cref="GitReadException"/> with
    /// <see cref="GitReadFailure.ObjectMissing"/> and leaves the reader usable; the reader never fetches a missing object.
    /// Lost framing, an early end or a cancellation stops the reader: every later read throws <see cref="IOException"/>.
    /// </remarks>
    /// <gotcha>In a partial clone, each read first starts one <c>git rev-list</c> child to check that the object is present.</gotcha>
    public async Task<byte[]> ReadAsync(string objectId, long maximumBytes, CancellationToken cancellationToken)
    {
        // A line break in the ID would desynchronize the framing.
        if (!GitPaths.IsObjectId(objectId, this.format))
            throw new ArgumentException("A blob read needs a full object ID of the repository's format.", nameof(objectId));
        // The bytes go into one array.
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, Array.MaxLength);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        if (this.faulted) throw new IOException("The object reader stopped after an earlier failure.");
        // In a partial clone, cat-file would fetch a missing object, so its presence is checked first.
        if (this.partialClone
            && !await GitObjects.IsPresentAsync(this.git, this.worktree, objectId, cancellationToken).ConfigureAwait(false))
            throw ObjectMissing();
        bool usable = false;
        try
        {
            await this.input.WriteAsync(Encoding.ASCII.GetBytes(objectId + "\n"), cancellationToken).ConfigureAwait(false);
            await this.input.FlushAsync(cancellationToken).ConfigureAwait(false);
            string header = await this.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (header == objectId + " missing")
            {
                // Git sends no body for a missing object, so the reader stays in step.
                usable = true;
                throw ObjectMissing();
            }
            string[] fields = header.Split(' ');
            if (fields is not [_, "blob", var size] || fields[0] != objectId
                || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long length))
                throw new InvalidDataException("Git's object reader returned an unexpected header.");
            if (length > maximumBytes)
                throw new GitReadException(GitReadFailure.LimitExceeded, "The committed file is larger than the limit; nothing was read.");
            byte[] body = new byte[length];
            await this.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            byte[] terminator = new byte[1];
            await this.ReadExactlyAsync(terminator, cancellationToken).ConfigureAwait(false);
            if (terminator[0] != (byte)'\n') throw new InvalidDataException("Git's object reader returned an unexpected header.");
            usable = true;
            return body;
        }
        finally
        {
            if (!usable) this.faulted = true;
        }
    }

    // Git fetches a missing object lazily only from a promisor remote. A clone with --filter records
    // remote.<name>.promisor; extensions.partialClone is the older marker. Git reads a key with no value as true, the true
    // words in any letter case, and a nonzero integer.
    private static async Task<bool> IsPartialCloneAsync(GitCommands git, string worktree, CancellationToken cancellationToken)
    {
        GitRunResult result = await git.RunReadAsync(worktree,
            ["config", "--get-regexp", @"^(extensions\.partialclone|remote\..+\.promisor)$"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Exit is { ExitCode: 1, Diagnostic: null } && result.Output.Length == 0) return false;
        GitOperationChecks.Check(result, "read the repository configuration");
        foreach (string line in Encoding.UTF8.GetString(result.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("extensions.partialclone ", StringComparison.Ordinal)) return true;
            if (!line.StartsWith("remote.", StringComparison.Ordinal)) continue;
            int key = line.LastIndexOf(".promisor", StringComparison.Ordinal);
            if (key < 0) continue;
            string rest = line[(key + ".promisor".Length)..];
            if (rest.Length == 0 || rest[0] == ' ' && IsTrue(rest[1..])) return true;
        }
        return false;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) && number != 0;

    private static GitReadException ObjectMissing() =>
        new(GitReadFailure.ObjectMissing, "The object is not in the local object store; nothing was fetched.");

    /// <summary>Closes standard input, so an idle reader exits, stops a child still running and waits for the drain.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
        try { await this.input.DisposeAsync().ConfigureAwait(false); }
        catch (IOException) { }
        await this.process.DisposeAsync().ConfigureAwait(false);
        try { await this.drain.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task<string> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = Array.IndexOf(this.buffer, (byte)'\n', this.start, this.end - this.start);
            if (newline >= 0)
            {
                string header = Encoding.ASCII.GetString(this.buffer, this.start, newline - this.start);
                this.start = newline + 1;
                return header;
            }
            if (this.end - this.start >= HeaderLimit) throw new InvalidDataException("Git's object reader returned an unexpected header.");
            if (await this.FillAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new IOException("Git's object reader ended before the object did.");
        }
    }

    private async Task ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
    {
        int copied = Math.Min(destination.Length, this.end - this.start);
        Array.Copy(this.buffer, this.start, destination, 0, copied);
        this.start += copied;
        while (copied < destination.Length)
        {
            int read = await this.output.ReadAsync(destination.AsMemory(copied), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("Git's object reader ended before the object did.");
            copied += read;
        }
    }

    // Moves the unread bytes to the front and reads more after them.
    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        if (this.start > 0)
        {
            Array.Copy(this.buffer, this.start, this.buffer, 0, this.end - this.start);
            this.end -= this.start;
            this.start = 0;
        }
        int read = await this.output.ReadAsync(this.buffer.AsMemory(this.end), cancellationToken).ConfigureAwait(false);
        this.end += read;
        return read;
    }
}
