using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

public sealed partial class ServicePlatform
{
    /// <inheritdoc />
    public async Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.Validate(installation);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            // PostgreSQL's scratch root is the user's LocalAppData\Temp, read from the shell so a nested subscriber's own
            // TEMP never selects it; a failed read is an installation failure, never a fallback.
            string userTemporaryDirectory = installation.Family == ServiceFamily.PostgreSql ? WindowsNative.UserTemporaryDirectory() : string.Empty;
            return await ServiceInstaller.InstallAsync(new WindowsServiceFileSystem(), installation, home, userTemporaryDirectory, cancellationToken).ConfigureAwait(false);
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
        => ServiceStartupFiles.WriteFailureAsync(new WindowsServiceFileSystem(), start, failure, cancellationToken);
    /// <inheritdoc />
    public Task<IServiceStoreInspection?> TryInspectStoppedStoreAsync(ServiceRegistration registration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceInstaller.RequireInstalled(new WindowsServiceFileSystem(), registration);
        string store = Path.Combine(ServiceInstaller.PluginDataOf(registration), "store");
        WindowsServiceOwner? lease = WindowsServiceOwner.TryAcquire(registration.RuntimePath, store, allowRemoval: true);
        return Task.FromResult<IServiceStoreInspection?>(lease is null ? null : new Inspection(registration.StoreId, store, ServiceInstaller.PluginDataOf(registration), lease));
    }
    private sealed class Inspection(Guid id, string path, string ownerPath, WindowsServiceOwner lease) : IServiceStoreInspection
    {
        public Guid StoreId => id;
        public string StorePath => path;
        public Task<ReadOnlyMemory<byte>?> ReadStoreStateAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(!lease.IsHeld, this);
            return ServiceStoreStateFiles.ReadAsync(new WindowsServiceFileSystem(), path, cancellationToken);
        }
        public Task RemoveOwnerFolderAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(!lease.IsHeld, this);
            return ServiceStoreStateFiles.RemoveOwnerFolderAsync(new WindowsServiceFileSystem(), ownerPath, cancellationToken);
        }
        public ValueTask DisposeAsync() { lease.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <inheritdoc />
    public Task WaitUntilStoppedAsync(string pluginData, CancellationToken cancellationToken = default)
        => ServiceInstaller.WaitUntilStoppedAsync(new WindowsServiceFileSystem(), pluginData, cancellationToken);
}
