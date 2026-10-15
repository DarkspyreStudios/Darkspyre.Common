
namespace Darkspyre.LocalServices;

/// <summary>A shared OS pin on one verified immutable payload. Pins survive cache object disposal.</summary>
public sealed class ServicePackagePin : IDisposable
{
    private readonly IServicePackageLease lease;

    internal ServicePackagePin(string cachePath, string packageDirectory, string buildDigest, ServicePackageManifest manifest, IServicePackageLease lease)
    {
        this.CachePath = cachePath;
        this.PackageDirectory = packageDirectory;
        this.BuildDigest = buildDigest;
        this.ExecutablePath = Path.Combine(packageDirectory, manifest.EntryPoint);
        this.ExecutableSha256 = manifest.Files.Single(file => file.Path == manifest.EntryPoint).Sha256;
        this.lease = lease;
    }

    /// <summary>The cache root containing the pinned package.</summary>
    public string CachePath { get; }
    /// <summary>The verified immutable package directory.</summary>
    public string PackageDirectory { get; }
    /// <summary>The digest identifying the pinned package manifest.</summary>
    public string BuildDigest { get; }
    /// <summary>The verified service entry point path.</summary>
    public string ExecutablePath { get; }
    /// <summary>The expected entry point digest.</summary>
    public string ExecutableSha256 { get; }
    /// <summary>Whether the underlying package lease is held.</summary>
    public bool IsHeld => this.lease.IsHeld;
    internal IServicePackageLease Lease => this.lease;

    /// <summary>Creates a launch specification while the package remains pinned.</summary>
    public ServiceLaunchSpec CreateLaunchSpec(IReadOnlyList<string> arguments, string userHome, string scratchDirectory)
    {
        ObjectDisposedException.ThrowIf(!this.IsHeld, this);
        return new(this.ExecutablePath, this.ExecutableSha256, arguments, userHome, scratchDirectory);
    }

    /// <summary>Releases this package lease.</summary>
    public void Dispose() => this.lease.Dispose();
}
