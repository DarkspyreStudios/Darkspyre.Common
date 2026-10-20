using System.Security.Cryptography;
using System.Text;

namespace Darkspyre.LocalServices;

/// <summary>A service-owned immutable payload cache. All metadata changes serialize through one catalog lease.</summary>
internal sealed partial class ServicePackageCache : IDisposable
{
    private readonly ServiceFileSystem fileSystem;
    private readonly ServicePackageFiles files;
    private readonly ServiceDirectory root;
    private readonly ServiceDirectory builds;
    private readonly ServiceDirectory leases;
    private const string StagingPrefix = ".install-";

    private ServicePackageCache(ServiceFileSystem fileSystem, ServiceDirectory root, ServiceDirectory builds, ServiceDirectory leases)
    {
        this.fileSystem = fileSystem;
        this.files = new(fileSystem);
        this.root = root;
        this.builds = builds;
        this.leases = leases;
    }

    public string Path => this.root.Path;

    public static ServicePackageCache Open(ServiceFileSystem fileSystem, string path)
    {
        ServiceDirectory root = fileSystem.OpenPrivateDirectory(path);
        ServiceDirectory? builds = null, leases = null;
        try
        {
            builds = fileSystem.OpenPrivateDirectory(System.IO.Path.Combine(root.Path, "builds"));
            leases = fileSystem.OpenPrivateDirectory(System.IO.Path.Combine(root.Path, "leases"));
            return new(fileSystem, root, builds, leases);
        }
        catch { leases?.Dispose(); builds?.Dispose(); root.Dispose(); throw; }
    }

    /// <summary>Copies a complete verified payload and returns a pin. It does not change the preferred build.</summary>
    public async Task<ServicePackagePin> InstallAsync(string sourceDirectory, ServicePackageManifest manifest,
        string expectedBuildDigest, CancellationToken cancellationToken = default)
    {
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await InstallLockedAsync(sourceDirectory, manifest, expectedBuildDigest, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ServicePackagePin> InstallLockedAsync(string sourceDirectory, ServicePackageManifest manifest,
        string expectedBuildDigest, CancellationToken cancellationToken)
    {
        byte[] manifestBytes = manifest.CanonicalBytes();
        manifest = ServicePackageManifest.Parse(manifestBytes); // Own a snapshot independent of caller mutation.
        string digest = Digest(expectedBuildDigest);
        if (Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != digest)
            throw new InvalidDataException("The payload manifest does not match its registered build identity.");
        string source = this.files.Root(sourceDirectory);
        string finalPath = BuildPath(digest);
        if (Exists(finalPath)) return await PinLockedAsync(digest, cancellationToken).ConfigureAwait(false);
        await this.files.VerifyAsync(source, manifest, cached: false, cancellationToken).ConfigureAwait(false);
        string temporaryName = StagingPrefix + Guid.NewGuid().ToString("N");
        string temporaryPath = System.IO.Path.Combine(this.builds.Path, temporaryName);
        using (this.fileSystem.OpenPrivateDirectory(temporaryPath)) { }
        bool published = false;
        Exception? failure = null;
        try
        {
            await CopyPayloadAsync(source, temporaryPath, manifest, manifestBytes, cancellationToken).ConfigureAwait(false);
            this.files.Seal(temporaryPath, manifest);
            await VerifyCachedAsync(temporaryPath, digest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.builds.PublishDirectory(temporaryName, digest);
            published = true;
        }
        catch (Exception error) { failure = error; }
        if (!published)
        {
            try { this.fileSystem.RemoveTree(temporaryPath); }
            catch (Exception cleanup) { throw new AggregateException(failure!, cleanup); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return await PinLockedAsync(digest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServicePackagePin> PinAsync(string buildDigest, CancellationToken cancellationToken = default)
    {
        string digest = Digest(buildDigest);
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await PinLockedAsync(digest, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPreferredAsync(string buildDigest, CancellationToken cancellationToken = default)
    {
        string digest = Digest(buildDigest);
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        await SetPreferredLockedAsync(digest, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetPreferredLockedAsync(string digest, CancellationToken cancellationToken)
    {
        using ServicePackagePin pin = await PinLockedAsync(digest, cancellationToken).ConfigureAwait(false);
        this.root.PublishPrivateFile("preferred", Encoding.ASCII.GetBytes(digest));
    }

    public async Task<ServicePackagePin> PinPreferredAsync(CancellationToken cancellationToken = default)
    {
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await PinLockedAsync(Preferred() ?? throw new FileNotFoundException("No service build is selected."), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes only a nonpreferred build with no launch/runtime pins. Lease files remain permanent.</summary>
    public async Task<bool> TryRemoveAsync(string buildDigest, CancellationToken cancellationToken = default)
    {
        string digest = Digest(buildDigest);
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        return RemoveLocked(digest, Preferred());
    }

    /// <summary>
    /// Removes every build except the preferred one that no launch or running service pins, and returns how many it
    /// removed. The caller holds the catalog lease. A subscriber whose build is removed installs it again on its next
    /// connection.
    /// </summary>
    internal int CollectUnpreferredLocked()
    {
        string? preferred = Preferred();
        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(this.builds.Path))
        {
            string name = System.IO.Path.GetFileName(directory);
            if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal) && RemoveLocked(Digest(name), preferred)) removed++;
        }
        return removed;
    }

    private bool RemoveLocked(string digest, string? preferred)
    {
        if (preferred == digest) return false;
        using IServicePackageLease? unused = this.leases.TryAcquire(digest + ".lock");
        if (unused is null || !Exists(BuildPath(digest))) return false;
        using (var handle = this.fileSystem.Open(BuildPath(digest), directory: true))
            this.fileSystem.InspectPrivate(handle, directory: true, readOnly: true);
        this.fileSystem.RemoveTree(BuildPath(digest));
        return true;
    }

    private async Task<ServicePackagePin> PinLockedAsync(string digest, CancellationToken token)
    {
        IServicePackageLease pin = this.leases.TryAcquire(digest + ".lock", shared: true)
            ?? throw new IOException("The service build is being collected.");
        try
        {
            ServicePackageManifest manifest = await VerifyCachedAsync(BuildPath(digest), digest, token).ConfigureAwait(false);
            return new ServicePackagePin(this.Path, BuildPath(digest), digest, manifest, pin);
        }
        catch { pin.Dispose(); throw; }
    }

    internal async Task<IServicePackageLease> AcquireCatalogAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Contracts.ServiceProtocol.ConnectDeadline);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            IServicePackageLease? lease = this.root.TryAcquire("catalog.lock");
            if (lease is not null) return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
        }
    }

    private string? Preferred()
    {
        byte[]? bytes = this.root.ReadPrivateFile("preferred");
        return bytes is null ? null : Digest(Encoding.UTF8.GetString(bytes));
    }

    private string BuildPath(string digest) => System.IO.Path.Combine(this.builds.Path, digest);
    private static string Digest(string value) { ServicePackageManifest.ValidateDigest(value); return value.ToLowerInvariant(); }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public void Dispose()
    {
        this.leases.Dispose();
        this.builds.Dispose();
        this.root.Dispose();
    }
}
