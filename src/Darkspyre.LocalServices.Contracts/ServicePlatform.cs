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
public sealed record ServiceStart(ServiceFamily Family, Guid StoreId, string LayoutId, string RuntimePath,
    string StorePath, string PackageCachePath, string BuildDigest, Guid AttemptId);
/// <summary>A bundled payload and its trusted manifest identity, installed by the subscriber library.</summary>
/// <param name="ApplicationName">The subscriber application name used for installation.</param>
/// <param name="Family">The fixed service family.</param>
/// <param name="LayoutId">The installed layout identity.</param>
/// <param name="ClientBuildDigest">The subscriber’s expected package digest.</param>
/// <param name="PackageDirectory">The bundled immutable payload directory.</param>
/// <param name="Manifest">The trusted payload inventory.</param>
/// <param name="ServiceBuildDigest">The service package digest used by the installer.</param>
public sealed record ServicePackageInstallation(string ApplicationName, ServiceFamily Family, string LayoutId,
    string ClientBuildDigest, string PackageDirectory, ServicePackageManifest Manifest, string ServiceBuildDigest);
/// <summary>Native installation, connection and ownership operations selected at build time.</summary>
public interface IServicePlatform
{
    /// <summary>Verifies the package and retains the shared registration without launching a backend.</summary>
    Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default);
    /// <summary>Opens native resources for an installed subscriber registration.</summary>
    IServiceInstallation OpenInstallation(ServiceRegistration registration);
    /// <summary>Adopts the launch pin and acquires family then store ownership before storage opens.</summary>
    Task<IServiceOwner?> TryOwnAsync(ServiceStart start, CancellationToken cancellationToken = default);
}
/// <summary>Native subscriber operations; disposing the installation never kills a backend.</summary>
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
