namespace Darkspyre.Common.Git;

/// <summary>
/// Serializes changes per repository: one change at a time for each Git common directory. Share one instance among every
/// client of one process that changes the same repositories.
/// </summary>
public sealed class GitRepositoryLocks
{
    private static StringComparer Directories => StringComparer.Ordinal;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> repositories = new(Directories);

    /// <summary>Waits for the repository's lock. Disposing the result releases it.</summary>
    /// <gotcha>The key is <paramref name="commonDirectory"/> as given: pass a path resolved through the path policy.</gotcha>
    public async Task<IDisposable> EnterAsync(string commonDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
        string key = commonDirectory;
        Entry entry;
        lock (this.gate)
        {
            if (!this.repositories.TryGetValue(key, out entry!)) this.repositories.Add(key, entry = new());
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { this.Release(key, entry, acquired: false); throw; }
        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        lock (this.gate)
        {
            if (--entry.Users == 0)
            {
                this.repositories.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int Users { get; set; }
    }

    private sealed class Lease(GitRepositoryLocks owner, string key, Entry entry) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0) owner.Release(key, entry, acquired: true);
        }
    }
}
