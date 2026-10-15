
namespace Darkspyre.LocalServices.Platform.Linux;

/// <summary>Acquires family ownership before store ownership. Storage must not open until both are held.</summary>
internal sealed class LinuxServiceOwner : IDisposable
{
    private readonly LinuxFileLease familyLease;
    private readonly LinuxFileLease storeLease;
    private int endpointClaimed;

    private LinuxServiceOwner(LinuxPrivateDirectory runtime, LinuxFileLease familyLease, LinuxPrivateDirectory store, LinuxFileLease storeLease)
    {
        this.RuntimeDirectory = runtime;
        this.familyLease = familyLease;
        this.StoreDirectory = store;
        this.storeLease = storeLease;
    }

    public LinuxPrivateDirectory RuntimeDirectory { get; }
    public LinuxPrivateDirectory StoreDirectory { get; }
    public bool IsHeld => this.familyLease.IsHeld && this.storeLease.IsHeld;

    /// <summary>Returns null for an existing family owner. A store owned outside this family is an explicit conflict.</summary>
    public static LinuxServiceOwner? TryAcquire(string runtimePath, string storePath)
    {
        LinuxPrivateDirectory runtime = LinuxPrivateDirectory.OpenOrCreate(runtimePath);
        LinuxFileLease? familyLease = null;
        LinuxPrivateDirectory? store = null;
        LinuxFileLease? storeLease = null;
        try
        {
            familyLease = LinuxFileLease.TryAcquire(runtime, "owner.lock");
            if (familyLease is null) return null;
            store = LinuxPrivateDirectory.OpenOrCreate(storePath);
            if (runtime.Device == store.Device && runtime.Inode == store.Inode)
                throw new ArgumentException("Runtime discovery and store data require distinct directories.", nameof(storePath));
            storeLease = LinuxFileLease.TryAcquire(store, "store.lock");
            if (storeLease is null) throw new StoreInUseException();
            var result = new LinuxServiceOwner(runtime, familyLease, store, storeLease);
            runtime = null!;
            familyLease = null;
            store = null;
            storeLease = null;
            return result;
        }
        finally
        {
            storeLease?.Dispose();
            store?.Dispose();
            familyLease?.Dispose();
            runtime?.Dispose();
        }
    }

    internal void ClaimEndpoint()
    {
        if (!this.IsHeld || Interlocked.CompareExchange(ref this.endpointClaimed, 1, 0) != 0)
            throw new InvalidOperationException("A service owner can publish only one endpoint during its lifetime.");
    }

    public void Dispose()
    {
        // The hosting layer closes all sockets and SQLite handles before disposing ownership.
        try { this.storeLease.Dispose(); }
        finally
        {
            try { this.StoreDirectory.Dispose(); }
            finally
            {
                try { this.familyLease.Dispose(); }
                finally { this.RuntimeDirectory.Dispose(); }
            }
        }
    }
}
