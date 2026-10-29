using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>Trusted installed identity, paths and arguments supplied by the subscriber library.</summary>
/// <param name="Family">The fixed service family.</param>
/// <param name="RuntimePath">The private runtime directory.</param>
/// <param name="PackageCachePath">The immutable payload cache root.</param>
/// <param name="StoreId">The durable store identity.</param>
/// <param name="LayoutId">The installed layout identity.</param>
/// <param name="ClientBuildDigest">The subscriber’s expected package digest.</param>
/// <param name="UserHome">The resolved user home for the detached service.</param>
/// <param name="ScratchDirectory">The service-owned scratch directory.</param>
/// <param name="BuildArguments">Constructs launch arguments from a pinned package and reserved attempt.</param>
public sealed record ServiceRegistration(ServiceFamily Family, string RuntimePath, string PackageCachePath,
    Guid StoreId, string LayoutId, string ClientBuildDigest, string UserHome, string ScratchDirectory,
    Func<ServicePackagePin, Guid, IReadOnlyList<string>> BuildArguments);
/// <summary>Registered identity passed to a detached backend at launch.</summary>
/// <param name="Family">The fixed service family.</param>
/// <param name="StoreId">The durable store identity.</param>
/// <param name="LayoutId">The installed layout identity.</param>
/// <param name="RuntimePath">The private runtime directory.</param>
/// <param name="StorePath">The durable service store path.</param>
/// <param name="PackageCachePath">The immutable payload cache root.</param>
/// <param name="BuildDigest">The verified service package digest.</param>
/// <param name="AttemptId">The reserved launch attempt identity.</param>
/// <param name="ModelPath">The absolute Model files folder the service loads models from, from the registration's
/// <c>--model-path</c> argument, or null when the registration records none.</param>
public sealed record ServiceStart(ServiceFamily Family, Guid StoreId, string LayoutId, string RuntimePath,
    string StorePath, string PackageCachePath, string BuildDigest, Guid AttemptId, string? ModelPath = null);
/// <summary>The folders a service's owner names, read from its settings each time it installs.</summary>
/// <param name="PluginData">The owner's absolute Plugin data folder. The store is <c>&lt;PluginData&gt;/store</c>; the
/// registration, program copies, connection point and scratch are in the library-created <c>&lt;PluginData&gt;/service</c>.</param>
/// <param name="ModelFiles">The absolute Model files folder the service loads models from, or null to keep the one the
/// registration records.</param>
/// <param name="CreateIfMissing">True for the service's owner. False for a program that joins what the owner set up:
/// it creates no folder and fails with <see cref="ServiceErrorCode.NotInstalled"/> when no registration is there.</param>
public sealed record ServiceFolders(string PluginData, string? ModelFiles, bool CreateIfMissing);
/// <summary>A bundled payload and its trusted manifest identity, installed by the subscriber library.</summary>
/// <param name="Family">The fixed service family.</param>
/// <param name="LayoutId">The installed layout identity.</param>
/// <param name="ClientBuildDigest">The subscriber’s expected package digest.</param>
/// <param name="PackageDirectory">The bundled immutable payload directory.</param>
/// <param name="Manifest">The trusted payload inventory.</param>
/// <param name="ServiceBuildDigest">The service package digest used by the installer.</param>
/// <param name="Folders">The owner's folders: the service keeps every file it writes under them.</param>
/// <param name="StoreIdentity">Reads the identity the store in the given absolute store folder records, or returns null
/// when the folder holds no store. The installer calls it only when it creates the registration, so a registration created
/// beside an existing store takes that store's identity. Null, or a null result, gives the registration a new identity.</param>
/// <gotcha>An exception <paramref name="StoreIdentity"/> throws fails the installation, and no registration is written.</gotcha>
public sealed record ServicePackageInstallation(ServiceFamily Family, string LayoutId, string ClientBuildDigest,
    string PackageDirectory, ServicePackageManifest Manifest, string ServiceBuildDigest, ServiceFolders Folders,
    Func<string, Guid?>? StoreIdentity = null);
/// <summary>Native installation, connection and ownership operations selected at build time.</summary>
public interface IServicePlatform
{
    /// <summary>Verifies the package and retains the shared registration without launching a backend.</summary>
    Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default);
    /// <summary>Opens native resources for an installed subscriber registration.</summary>
    IServiceInstallation OpenInstallation(ServiceRegistration registration);
    /// <summary>Adopts the launch pin and acquires family then store ownership before storage opens.</summary>
    Task<IServiceOwner?> TryOwnAsync(ServiceStart start, CancellationToken cancellationToken = default);
    /// <summary>Completes once no process owns the service whose owner folder is <paramref name="pluginData"/>; at once
    /// when its runtime folder does not exist. Creates nothing.</summary>
    Task WaitUntilStoppedAsync(string pluginData, CancellationToken cancellationToken = default);
}
/// <summary>Native subscriber operations; disposing the installation never kills a backend.</summary>
/// <gotcha>Every operation first reads the registration and throws <see cref="ServiceErrorCode.NotInstalled"/> when it is
/// gone, so a connection or launch never recreates a moved or removed folder.</gotcha>
public interface IServiceInstallation : IDisposable
{
    /// <summary>Authenticates an existing owner, returning null only for an unavailable transport.</summary>
    Task<ServicePeer?> TryConnectAsync(CancellationToken cancellationToken = default);
    /// <summary>Reserves a launch under the shared launch budget.</summary>
    Task<ServiceLaunchReservation> TryReserveLaunchAsync(CancellationToken cancellationToken = default);
    /// <summary>Pins the preferred verified package for launch.</summary>
    Task<ServicePackagePin> PinPreferredAsync(CancellationToken cancellationToken = default);
    /// <summary>Launches independently of the subscriber and returns an exit observation.</summary>
    ServiceLaunchObservation Launch(ServicePackagePin pin, Guid attemptId);
    /// <summary>Records the observed exit without changing service ownership.</summary>
    Task RecordExitAsync(Guid attemptId, bool normalExit, CancellationToken cancellationToken = default);
    /// <summary>Resets accounting on an explicit retry.</summary>
    Task ResetCrashBudgetAsync(CancellationToken cancellationToken = default);
}
/// <summary>Backend ownership retained until domain work and all peers close.</summary>
public interface IServiceOwner : IAsyncDisposable
{
    /// <summary>The identity of this ownership lifetime.</summary>
    Guid Epoch { get; }
    /// <summary>The private owned storage directory.</summary>
    string StorePath { get; }
    /// <summary>The private directory for transient request bodies.</summary>
    string StagingPath { get; }
    /// <summary>Publishes authenticated discovery after storage initialization succeeds.</summary>
    Task PublishAsync(CancellationToken cancellationToken = default);
    /// <summary>Accepts a peer after native identity and capability authentication.</summary>
    Task<ServicePeer> AcceptAsync(CancellationToken cancellationToken = default);
    /// <summary>Removes discovery, records normal exit and releases ownership after domain cleanup.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
/// <summary>An OS-held lease keeping an immutable package alive.</summary>
public interface IServicePackageLease : IDisposable
{
    /// <summary>Whether this lease still holds its native resource.</summary>
    bool IsHeld { get; }
}
/// <summary>Exit observation without authority to terminate the detached backend.</summary>
/// <param name="AttemptId">The reserved launch attempt identity.</param>
/// <param name="ProcessId">The detached process identity.</param>
/// <param name="BuildDigest">The verified service package digest.</param>
/// <param name="Completion">Completes when the detached process exits.</param>
public sealed record ServiceLaunchObservation(Guid AttemptId, int ProcessId, string BuildDigest, Task<ServiceProcessExit> Completion);
