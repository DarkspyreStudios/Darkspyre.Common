
namespace Darkspyre.LocalServices.Platform.Windows;

/// <summary>Acquires family ownership before store ownership. Storage must not open until both are held.</summary>
internal sealed class WindowsServiceOwner : IDisposable
{
    private readonly WindowsFileLease familyLease;
    private readonly WindowsFileLease storeLease;
    private int endpointClaimed;

    private WindowsServiceOwner(WindowsPrivateDirectory runtime, WindowsFileLease familyLease, WindowsPrivateDirectory store, WindowsFileLease storeLease)
    {
        this.RuntimeDirectory = runtime;
        this.familyLease = familyLease;
        this.StoreDirectory = store;
        this.storeLease = storeLease;
    }

    public WindowsPrivateDirectory RuntimeDirectory { get; }
    public WindowsPrivateDirectory StoreDirectory { get; }
    public bool IsHeld => this.familyLease.IsHeld && this.storeLease.IsHeld;

    /// <summary>Returns null for an existing family owner. A store owned outside this family is an explicit conflict.</summary>
    public static WindowsServiceOwner? TryAcquire(string runtimePath, string storePath)
    {
        WindowsPrivateDirectory runtime = WindowsPrivateDirectory.OpenOrCreate(runtimePath);
        WindowsFileLease? familyLease = null;
        WindowsPrivateDirectory? store = null;
        WindowsFileLease? storeLease = null;
        try
        {
            familyLease = WindowsFileLease.TryAcquire(runtime, "owner.lock");
            if (familyLease is null) return null;
            store = WindowsPrivateDirectory.OpenOrCreate(storePath);
            if (runtime.Identity.SameFile(store.Identity))
                throw new ArgumentException("Runtime discovery and store data require distinct directories.", nameof(storePath));
            storeLease = WindowsFileLease.TryAcquire(store, "store.lock");
            if (storeLease is null) throw new StoreInUseException();
            var result = new WindowsServiceOwner(runtime, familyLease, store, storeLease);
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
