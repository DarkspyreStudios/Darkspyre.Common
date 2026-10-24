using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

public sealed partial class ServicePlatform
{
    /// <inheritdoc />
    public async Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.Validate(installation);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The installation root holds the registration, the package cache, scratch and rendezvous. The store follows the caller's data root.
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Darkspyre", installation.ApplicationName);
        try
        {
            return await ServiceInstaller.InstallAsync(new WindowsServiceFileSystem(), installation, root, Path.Combine(root, "runtime"), home, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceProtocolException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "The packaged search service could not be installed.", error);
        }
    }
}
