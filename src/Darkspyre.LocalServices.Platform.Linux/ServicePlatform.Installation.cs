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
            return await ServiceInstaller.InstallAsync(new LinuxServiceFileSystem(), installation, home, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceProtocolException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "The packaged search service could not be installed.", error);
        }
    }

    /// <inheritdoc />
    public Task WaitUntilStoppedAsync(string pluginData, CancellationToken cancellationToken = default)
        => ServiceInstaller.WaitUntilStoppedAsync(new LinuxServiceFileSystem(), pluginData, cancellationToken);
}
