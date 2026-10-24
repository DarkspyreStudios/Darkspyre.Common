using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

// Registration and preferred selection share the cache catalog lock on every platform.
internal static class ServiceInstaller
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    // The installation root holds the registration, the package cache and scratch; the store lives under the caller's data root.
    internal static async Task<ServiceRegistration> InstallAsync(ServiceFileSystem files,
        ServicePackageInstallation installation, string installationRoot, string runtimePath, string userHome,
        CancellationToken cancellationToken)
    {
        using ServiceDirectory root = files.OpenPrivateDirectory(installationRoot);
        string cachePath = Path.Combine(installationRoot, "packages");
        string? storePath = installation.DataRoot is { } dataRoot ? Path.Combine(Path.GetFullPath(dataRoot), "store") : null;
        using ServicePackageCache cache = ServicePackageCache.Open(files, cachePath);
        using IServicePackageLease catalog = await cache.AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        using ServicePackagePin pin = await cache.InstallLockedAsync(installation.PackageDirectory,
            installation.Manifest, installation.ServiceBuildDigest, cancellationToken).ConfigureAwait(false);
        byte[]? bytes = root.ReadPrivateFile("registration.json");
        Registration stored;
        if (bytes is null)
        {
            // A service whose subscribers name no data root keeps no durable data; its store folder holds only locks and staging.
            stored = new(Guid.NewGuid(), storePath ?? Path.Combine(installationRoot, "store"), runtimePath, cachePath, installation.LayoutId);
            root.PublishPrivateFile("registration.json", JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        }
        else
        {
            stored = JsonSerializer.Deserialize<Registration>(bytes, Json)
                ?? throw new InvalidDataException("The service registration is missing.");
            if (stored.StoreId == Guid.Empty || !Path.IsPathFullyQualified(stored.StorePath)
                || !Path.IsPathFullyQualified(stored.RuntimePath) || stored.PackageCachePath != cachePath)
                throw new InvalidDataException("The service registration is invalid.");
            if (stored.LayoutId != installation.LayoutId)
                throw new ServiceProtocolException(ServiceErrorCode.IncompatibleProtocol);
            if (storePath is not null && !string.Equals(storePath, stored.StorePath, StringComparison.Ordinal))
            {
                // The next launch opens the store here. A running service keeps the store it opened.
                stored = stored with { StorePath = storePath };
                root.PublishPrivateFile("registration.json", JsonSerializer.SerializeToUtf8Bytes(stored, Json));
            }
        }
        await cache.SetPreferredLockedAsync(pin.BuildDigest, cancellationToken).ConfigureAwait(false);
        // Builds no launch can select any more leave the cache; a running service's pinned build stays.
        cache.CollectUnpreferredLocked();
        string scratch = Path.Combine(installationRoot, "scratch");
        using (files.OpenPrivateDirectory(scratch)) { }
        return new(installation.Family, stored.RuntimePath, stored.PackageCachePath, stored.StoreId,
            stored.LayoutId, installation.ClientBuildDigest, userHome, scratch, (package, attempt) =>
            [
                "--family", installation.Family.ToString(), "--store-id", stored.StoreId.ToString("D"),
                "--layout-id", stored.LayoutId, "--runtime-path", stored.RuntimePath,
                "--store-path", stored.StorePath, "--package-cache-path", stored.PackageCachePath,
                "--build-digest", package.BuildDigest, "--attempt-id", attempt.ToString("D"),
            ]);
    }

    internal static void Validate(ServicePackageInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.IsNullOrWhiteSpace(installation.ApplicationName) || installation.ApplicationName.Any(c => !char.IsAsciiLetterOrDigit(c))
            || !Enum.IsDefined(installation.Family) || string.IsNullOrWhiteSpace(installation.LayoutId)
            || installation.LayoutId.Length > 128 || installation.LayoutId.Any(char.IsControl))
            throw new ArgumentException("A service installation requires an application, family and layout identity.", nameof(installation));
        if (installation.DataRoot is { } dataRoot && (!Path.IsPathFullyQualified(dataRoot) || dataRoot.Any(char.IsControl)))
            throw new ArgumentException("A service data root must be an absolute path.", nameof(installation));
        ServicePackageManifest.ValidateDigest(installation.ClientBuildDigest);
    }

    private sealed record Registration(Guid StoreId, string StorePath, string RuntimePath, string PackageCachePath, string LayoutId);
}
