using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Linux;

public sealed partial class ServicePlatform
{
    /// <inheritdoc />
    public async Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.Validate(installation);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // Linux reads $XDG_RUNTIME_DIR inside the installer for PostgreSQL, so no platform-resolved temporary root is passed.
            return await ServiceInstaller.InstallAsync(new LinuxServiceFileSystem(), installation, home, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceProtocolException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "The packaged search service could not be installed.", error);
        }
    }

    /// <inheritdoc />
    public Task WriteStartupFailureAsync(ServiceStart start, ServiceStartupFailure failure, CancellationToken cancellationToken = default)
        => ServiceStartupFiles.WriteFailureAsync(new LinuxServiceFileSystem(), start, failure, cancellationToken);
    /// <inheritdoc />
    public Task<IServiceStoreInspection?> TryInspectStoppedStoreAsync(ServiceRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceInstaller.RequireInstalled(new LinuxServiceFileSystem(), registration);
        string store = Path.Combine(ServiceInstaller.PluginDataOf(registration), "store");
        LinuxServiceOwner? lease = LinuxServiceOwner.TryAcquire(registration.RuntimePath, store);
        return Task.FromResult<IServiceStoreInspection?>(lease is null ? null : new Inspection(registration.StoreId, store, ServiceInstaller.PluginDataOf(registration), lease));
    }
    private sealed class Inspection(Guid id, string path, string ownerPath, LinuxServiceOwner lease) : IServiceStoreInspection
    {
        public Guid StoreId => id;
        public string StorePath => path;
        public Task<ReadOnlyMemory<byte>?> ReadStoreStateAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(!lease.IsHeld, this);
            return ServiceStoreStateFiles.ReadAsync(new LinuxServiceFileSystem(), path, cancellationToken);
        }
        public Task RemoveOwnerFolderAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(!lease.IsHeld, this);
            return ServiceStoreStateFiles.RemoveOwnerFolderAsync(new LinuxServiceFileSystem(), ownerPath, cancellationToken);
        }
        public ValueTask DisposeAsync() { lease.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <inheritdoc />
    public Task WaitUntilStoppedAsync(string pluginData, CancellationToken cancellationToken = default)
        => ServiceInstaller.WaitUntilStoppedAsync(new LinuxServiceFileSystem(), pluginData, cancellationToken);
}
