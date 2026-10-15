
namespace Darkspyre.LocalServices;

internal sealed partial class ServicePackageCache
{
    /// <summary>
    /// Called once by the backend before publishing discovery or opening storage. Takes ownership of the launcher's
    /// inherited pin and verifies the complete payload. A failed adoption is fatal to startup, not a fresh-pin fallback.
    /// </summary>
    public async Task<ServicePackagePin> AdoptLaunchPinAsync(string expectedBuildDigest, CancellationToken cancellationToken = default)
    {
        string digest = Digest(expectedBuildDigest);
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        IServicePackageLease? lease = this.fileSystem.AdoptLaunchLease(this.leases, digest, this.Path);
        try
        {
            ServicePackageManifest manifest = await VerifyCachedAsync(BuildPath(digest), digest, cancellationToken).ConfigureAwait(false);
            var pin = new ServicePackagePin(this.Path, BuildPath(digest), digest, manifest, lease);
            lease = null;
            return pin;
        }
        finally { lease?.Dispose(); }
    }
}
