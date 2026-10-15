namespace Darkspyre.LocalServices.Packaging;

/// <summary>Captures and refreshes service inventories in build and signing copies.</summary>
public static class ServicePackageBuild
{
    /// <summary>Captures the actual files, directories, hashes and executable flags of a service payload.</summary>
    /// <gotcha>The caller signs payload files before the final capture. This method starts no service.</gotcha>
    public static Task<ServicePackageManifest> CaptureAsync(string payloadDirectory, string entryPoint,
        CancellationToken cancellationToken = default)
        => new ServicePackageFiles(PackagingPlatform.Files()).CaptureAsync(payloadDirectory, entryPoint, cancellationToken);

    /// <summary>Refreshes bundled service manifests and returns the absolute paths of files it rewrites.</summary>
    /// <gotcha>The caller signs returned manifests before sealing the containing package or app.</gotcha>
    public static async Task<string[]> RefreshManifestsAsync(string directory, CancellationToken cancellationToken = default)
    {
        List<string> manifests = [], changed = [];
        Inspect(Path.GetFullPath(directory));
        foreach (string path in manifests.Order(StringComparer.Ordinal))
        {
            byte[] previous = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            ServicePackageManifest manifest = ServicePackageManifest.Parse(previous);
            ServicePackageManifest captured = await CaptureAsync(Path.Combine(Path.GetDirectoryName(path)!, "payload"),
                manifest.EntryPoint, cancellationToken).ConfigureAwait(false);
            byte[] current = captured.CanonicalBytes();
            if (previous.AsSpan().SequenceEqual(current)) continue;
            await File.WriteAllBytesAsync(path, current, cancellationToken).ConfigureAwait(false);
            changed.Add(path);
        }
        return [.. changed];

        void Inspect(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) Inspect(child);
            }
            else if (Path.GetFileName(path) == "manifest.json" && Path.GetFileName(Path.GetDirectoryName(path)) == "service")
                manifests.Add(path);
        }
    }
}
