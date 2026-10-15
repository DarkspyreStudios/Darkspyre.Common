using System.Runtime.Versioning;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>Acquires family ownership before store ownership. Storage must not open until both are held.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacServiceOwner : IDisposable
{
    private readonly MacFileLease familyLease;
    private readonly MacFileLease storeLease;
    private int endpointClaimed;

    private MacServiceOwner(MacPrivateDirectory runtime, MacFileLease familyLease, MacPrivateDirectory store, MacFileLease storeLease)
    {
        this.RuntimeDirectory = runtime;
        this.familyLease = familyLease;
        this.StoreDirectory = store;
        this.storeLease = storeLease;
    }

    /// <summary>The owned private runtime directory.</summary>
    public MacPrivateDirectory RuntimeDirectory { get; }
    /// <summary>The owned private store directory.</summary>
    public MacPrivateDirectory StoreDirectory { get; }
    /// <summary>Whether both the family and store leases remain held.</summary>
    public bool IsHeld => this.familyLease.IsHeld && this.storeLease.IsHeld;

    /// <summary>Returns null for an existing family owner. A store owned outside this family is an explicit conflict.</summary>
    public static MacServiceOwner? TryAcquire(string runtimePath, string storePath)
    {
        MacPrivateDirectory runtime = MacPrivateDirectory.OpenOrCreate(runtimePath);
        MacFileLease? familyLease = null;
        MacPrivateDirectory? store = null;
        MacFileLease? storeLease = null;
        try
        {
            familyLease = MacFileLease.TryAcquire(runtime, "owner.lock");
            if (familyLease is null) return null;
            store = MacPrivateDirectory.OpenOrCreate(storePath);
            if (runtime.Device == store.Device && runtime.Inode == store.Inode)
                throw new ArgumentException("Runtime discovery and store data require distinct directories.", nameof(storePath));
            storeLease = MacFileLease.TryAcquire(store, "store.lock");
            if (storeLease is null) throw new StoreInUseException();
            var result = new MacServiceOwner(runtime, familyLease, store, storeLease);
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

    /// <summary>Releases endpoint ownership and the store and family leases.</summary>
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
