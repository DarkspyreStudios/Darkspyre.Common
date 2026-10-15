namespace Darkspyre.LocalServices.Packaging;

internal static class PackagingPlatform
{
    internal static ServiceFileSystem Files()
    {
        if (OperatingSystem.IsMacOS())
            return new Platform.MacOS.MacServiceFileSystem();
        if (OperatingSystem.IsLinux())
            return new Platform.Linux.LinuxServiceFileSystem();
        if (OperatingSystem.IsWindows())
            return new Platform.Windows.WindowsServiceFileSystem();
        throw new PlatformNotSupportedException("Service package inspection is unavailable on this operating system.");
    }
}
