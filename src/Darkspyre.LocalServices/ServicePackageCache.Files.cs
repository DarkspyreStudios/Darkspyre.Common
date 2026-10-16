using System.Security.Cryptography;

namespace Darkspyre.LocalServices;

internal sealed partial class ServicePackageCache
{
    private async Task CopyPayloadAsync(string source, string destination, ServicePackageManifest manifest, byte[] manifestBytes, CancellationToken token)
    {
        foreach (string directory in manifest.Directories.OrderBy(path => path.Length))
            this.fileSystem.CreateDirectory(System.IO.Path.Combine(destination, directory));
        foreach (ServicePackageFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using var handle = this.fileSystem.Open(System.IO.Path.Combine(source, file.Path), directory: false);
            using var input = new FileStream(handle, FileAccess.Read);
            await using var output = this.fileSystem.CreateFile(System.IO.Path.Combine(destination, file.Path));
            string hash = await ServicePackageFiles.HashAsync(input, file.Length, output, token).ConfigureAwait(false);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The source payload changed during installation.");
            await output.FlushAsync(token).ConfigureAwait(false);
            this.fileSystem.SyncFile(output);
        }
        string metadata = System.IO.Path.Combine(destination, ServicePackageManifest.FileName);
        await using var manifestFile = this.fileSystem.CreateFile(metadata);
        await manifestFile.WriteAsync(manifestBytes, token).ConfigureAwait(false);
        await manifestFile.FlushAsync(token).ConfigureAwait(false);
        this.fileSystem.SyncFile(manifestFile);
        // One device flush covers every payload file synced above; the build is published only after it.
        this.fileSystem.FlushDevice(manifestFile);
    }

    private async Task<ServicePackageManifest> VerifyCachedAsync(string path, string digest, CancellationToken token)
    {
        using var handle = this.fileSystem.Open(System.IO.Path.Combine(path, ServicePackageManifest.FileName), directory: false);
        this.fileSystem.InspectPrivate(handle, directory: false, readOnly: true);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length is 0 or > ServicePackageManifest.MaximumManifestBytes) throw new InvalidDataException("The cached manifest exceeds its size limit.");
        byte[] bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        ServicePackageManifest manifest = ServicePackageManifest.Parse(bytes);
        byte[] canonical = manifest.CanonicalBytes();
        if (!bytes.AsSpan().SequenceEqual(canonical) || Convert.ToHexStringLower(SHA256.HashData(canonical)) != digest)
            throw new InvalidDataException("The cached manifest does not match its immutable build identity.");
        await this.files.VerifyAsync(path, manifest, cached: true, token).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Exclusive catalog ownership proves that no installer still uses these staging directories.</summary>
    public async Task<int> CollectAbandonedStagingAsync(CancellationToken cancellationToken = default)
    {
        using IServicePackageLease catalog = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        int removed = 0;
        foreach (string directory in Directory.EnumerateDirectories(this.builds.Path, StagingPrefix + "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(System.IO.Path.GetFileName(directory)[StagingPrefix.Length..], "N", out _))
                throw new InvalidDataException("The service cache has an unrecognized staging entry.");
            this.fileSystem.RemoveTree(directory);
            if (++removed == 128) break;
        }
        return removed;
    }
}
