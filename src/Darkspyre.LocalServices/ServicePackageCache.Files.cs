using System.Security.Cryptography;

namespace Darkspyre.LocalServices;

internal sealed partial class ServicePackageCache
{
    /// <summary>Captures a trusted build output inventory. A distribution must register/verify the returned digest.</summary>
    internal async Task<ServicePackageManifest> CaptureAsync(string sourceDirectory, string entryPoint, CancellationToken cancellationToken = default)
    {
        string source = this.files.Root(sourceDirectory);
        var inventory = this.files.Inventory(source, cached: false);
        List<ServicePackageFile> files = [];
        foreach (string path in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = this.fileSystem.Open(System.IO.Path.Combine(source, path), directory: false);
            using var input = new FileStream(handle, FileAccess.Read);
            long length = input.Length;
            string hash = await ServicePackageFiles.HashAsync(input, length, null, cancellationToken).ConfigureAwait(false);
            files.Add(new(path, length, hash, this.fileSystem.IsExecutable(System.IO.Path.Combine(source, path))));
        }
        var manifest = new ServicePackageManifest(ServicePackageManifest.CurrentFormat, entryPoint, inventory.Directories, [.. files]);
        manifest.Validate();
        return manifest;
    }

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
            output.Flush(flushToDisk: true);
        }
        string metadata = System.IO.Path.Combine(destination, ServicePackageManifest.FileName);
        await using var manifestFile = this.fileSystem.CreateFile(metadata);
        await manifestFile.WriteAsync(manifestBytes, token).ConfigureAwait(false);
        await manifestFile.FlushAsync(token).ConfigureAwait(false);
        manifestFile.Flush(flushToDisk: true);
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
