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
    public Task WaitUntilStoppedAsync(string pluginData, CancellationToken cancellationToken = default)
        => ServiceInstaller.WaitUntilStoppedAsync(new WindowsServiceFileSystem(), pluginData, cancellationToken);
}
