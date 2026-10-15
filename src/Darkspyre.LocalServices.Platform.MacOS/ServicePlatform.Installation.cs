using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.MacOS;

public sealed partial class ServicePlatform
{
    /// <inheritdoc />
    public async Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.Validate(installation);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = Path.Combine(home, "Library", "Application Support", "Darkspyre", installation.ApplicationName);
        // Rendezvous stays separate from durable data and uses a shorter per-user cache path.
        string runtime = Path.Combine(home, "Library", "Caches", "Darkspyre", installation.ApplicationName);
        try
        {
            return await ServiceInstaller.InstallAsync(new MacServiceFileSystem(), installation, root, runtime, home, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceProtocolException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "The packaged search service could not be installed.", error);
        }
    }
}
