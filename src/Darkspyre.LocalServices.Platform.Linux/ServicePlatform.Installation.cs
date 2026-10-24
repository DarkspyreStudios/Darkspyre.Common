using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Linux;

public sealed partial class ServicePlatform
{
    /// <inheritdoc />
    public async Task<ServiceRegistration> InstallAsync(ServicePackageInstallation installation, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.Validate(installation);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string data = string.IsNullOrEmpty(xdgData) ? Path.Combine(home, ".local", "share") : xdgData;
        // The installation root holds the registration, the package cache and scratch. The store follows the caller's data root.
        string root = Path.Combine(data, "darkspyre", installation.ApplicationName.ToLowerInvariant());
        string runtime = Path.Combine(root, "runtime");
        try
        {
            string? xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdgRuntime) && Directory.Exists(xdgRuntime))
            {
                using var directory = LinuxPrivateDirectory.OpenPath(xdgRuntime, directory: true);
                if (LinuxNative.Fstat(directory, out LinuxNative.FileStatus status) != 0)
                    throw LinuxNative.Error("inspect runtime directory", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
                if (status.UserId == LinuxNative.EffectiveUserId()) runtime = Path.Combine(xdgRuntime, "darkspyre-" + installation.ApplicationName.ToLowerInvariant());
            }
            return await ServiceInstaller.InstallAsync(new LinuxServiceFileSystem(), installation, root, runtime, home, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceProtocolException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable, "The packaged search service could not be installed.", error);
        }
    }
}
