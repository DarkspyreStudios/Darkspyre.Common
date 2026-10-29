using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

// Registration and preferred selection share the cache catalog lock on every platform. Every path derives from the owner's
// Plugin data folder <P>: the store is <P>/store, and the library keeps the registration, package cache, runtime and scratch
// in the private <P>/service it creates. The registration records no folder except Model files, so the folder can move.
internal static class ServiceInstaller
{
    private const string RegistrationFile = "registration.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    internal static async Task<ServiceRegistration> InstallAsync(ServiceFileSystem files,
        ServicePackageInstallation installation, string userHome, CancellationToken cancellationToken)
    {
        ServiceFolders folders = installation.Folders;
        string pluginData = Path.GetFullPath(folders.PluginData);
        string rootPath = ServiceRoot(pluginData);
        // A joiner creates nothing: a missing service folder or registration fails before anything is written.
        using ServiceDirectory root = folders.CreateIfMissing ? files.OpenPrivateDirectory(rootPath)
            : files.OpenExistingPrivateDirectory(rootPath) ?? throw NotInstalled(pluginData);
        if (!folders.CreateIfMissing && root.ReadPrivateFile(RegistrationFile) is null) throw NotInstalled(pluginData);
        string cachePath = Path.Combine(rootPath, "packages");
        using ServicePackageCache cache = ServicePackageCache.Open(files, cachePath);
        using IServicePackageLease catalog = await cache.AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
        using ServicePackagePin pin = await cache.InstallLockedAsync(installation.PackageDirectory,
            installation.Manifest, installation.ServiceBuildDigest, cancellationToken).ConfigureAwait(false);
        string? modelFiles = folders.ModelFiles is { } named ? Path.GetFullPath(named) : null;
        string storePath = Path.Combine(pluginData, "store");
        byte[]? bytes = root.ReadPrivateFile(RegistrationFile);
        Registration stored;
        if (bytes is null)
        {
            if (!folders.CreateIfMissing) throw NotInstalled(pluginData);
            // A store that is already there keeps its identity: the service refuses a store whose identity differs from the
            // registration's (StoreMismatch).
            stored = new(installation.StoreIdentity?.Invoke(storePath) ?? Guid.NewGuid(), installation.LayoutId, modelFiles);
            root.PublishPrivateFile(RegistrationFile, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        }
        else
        {
            stored = Parse(bytes);
            if (stored.LayoutId != installation.LayoutId)
                throw new ServiceProtocolException(ServiceErrorCode.IncompatibleProtocol);
            if (modelFiles is not null && !string.Equals(modelFiles, stored.ModelPath, StringComparison.Ordinal))
            {
                // The next launch passes this folder. A running service keeps the one it started with.
                stored = stored with { ModelPath = modelFiles };
                root.PublishPrivateFile(RegistrationFile, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
            }
        }
        await cache.SetPreferredLockedAsync(pin.BuildDigest, cancellationToken).ConfigureAwait(false);
        // Builds no launch can select any more leave the cache; a running service's pinned build stays.
        cache.CollectUnpreferredLocked();
        string runtime = Path.Combine(rootPath, "runtime");
        string scratch = Path.Combine(rootPath, "scratch");
        using (files.OpenPrivateDirectory(runtime)) { }
        using (files.OpenPrivateDirectory(scratch)) { }
        Guid storeId = stored.StoreId;
        string layoutId = stored.LayoutId;
        return new(installation.Family, runtime, cachePath, storeId, layoutId, installation.ClientBuildDigest, userHome,
            scratch, (package, attempt) =>
            {
                // Read at each launch, so a service that starts after a Model files change gets the folder recorded last.
                string? modelPath = ReadRegistration(files, rootPath).ModelPath;
                List<string> arguments =
                [
                    "--family", installation.Family.ToString(), "--store-id", storeId.ToString("D"),
                    "--layout-id", layoutId, "--runtime-path", runtime,
                    "--store-path", storePath, "--package-cache-path", cachePath,
                    "--build-digest", package.BuildDigest, "--attempt-id", attempt.ToString("D"),
                ];
                if (modelPath is not null) arguments.AddRange(["--model-path", modelPath]);
                return arguments;
            });
    }

    // Every installation operation calls this first, so a connection or launch never recreates a moved or removed folder.
    internal static void RequireInstalled(ServiceFileSystem files, ServiceRegistration registration)
        => _ = ReadRegistration(files, RootOf(registration));

    // The owner folder of an installed registration, whose runtime folder is <P>/service/runtime.
    internal static string PluginDataOf(ServiceRegistration registration)
        => Path.GetDirectoryName(RootOf(registration)) ?? throw new ArgumentException("The service registration has no owner folder.", nameof(registration));

    internal static async Task WaitUntilStoppedAsync(ServiceFileSystem files, string pluginData, CancellationToken cancellationToken)
    {
        if (!IsAbsolute(pluginData)) throw new ArgumentException("A Plugin data folder must be an absolute path.", nameof(pluginData));
        string runtime = Path.Combine(ServiceRoot(Path.GetFullPath(pluginData)), "runtime");
        while (true)
        {
            using (ServiceDirectory? directory = files.OpenExistingPrivateDirectory(runtime))
                if (directory is null || !directory.IsLeaseHeld("owner.lock")) return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void Validate(ServicePackageInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!Enum.IsDefined(installation.Family) || string.IsNullOrWhiteSpace(installation.LayoutId)
            || installation.LayoutId.Length > 128 || installation.LayoutId.Any(char.IsControl))
            throw new ArgumentException("A service installation requires a family and layout identity.", nameof(installation));
        if (installation.Folders is not { } folders || !IsAbsolute(folders.PluginData)
            || (folders.ModelFiles is { } modelFiles && !IsAbsolute(modelFiles)))
            throw new ArgumentException("A service installation requires an absolute Plugin data folder, and an absolute Model files folder when it names one.", nameof(installation));
        ServicePackageManifest.ValidateDigest(installation.ClientBuildDigest);
    }

    private static string ServiceRoot(string pluginData) => Path.Combine(pluginData, "service");

    private static string RootOf(ServiceRegistration registration)
        => Path.GetDirectoryName(registration.RuntimePath) ?? throw new ArgumentException("The service registration has no service folder.", nameof(registration));

    private static Registration ReadRegistration(ServiceFileSystem files, string rootPath)
    {
        using ServiceDirectory? root = files.OpenExistingPrivateDirectory(rootPath);
        byte[]? bytes = root?.ReadPrivateFile(RegistrationFile);
        return bytes is null ? throw NotInstalled(Path.GetDirectoryName(rootPath) ?? rootPath) : Parse(bytes);
    }

    private static Registration Parse(byte[] bytes)
    {
        Registration stored = JsonSerializer.Deserialize<Registration>(bytes, Json)
            ?? throw new InvalidDataException("The service registration is missing.");
        if (stored.StoreId == Guid.Empty || string.IsNullOrWhiteSpace(stored.LayoutId)
            || (stored.ModelPath is { } modelPath && !IsAbsolute(modelPath)))
            throw new InvalidDataException("The service registration is invalid.");
        return stored;
    }

    private static bool IsAbsolute(string? path) => path is not null && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl);

    private static ServiceProtocolException NotInstalled(string pluginData)
        => new(ServiceErrorCode.NotInstalled, $"No owner set up the service in {pluginData}.");

    private sealed record Registration(Guid StoreId, string LayoutId, string? ModelPath);
}
