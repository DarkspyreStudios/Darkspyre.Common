using System.Runtime.Versioning;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Platform.Windows;

[assembly: SupportedOSPlatform("windows")]

internal static class FixturePlatform
{
    internal static IServicePlatform Create() => new ServicePlatform();
    internal static ServicePackageCache OpenCache(string path) => ServicePackageCache.Open(new WindowsServiceFileSystem(), path);
}
