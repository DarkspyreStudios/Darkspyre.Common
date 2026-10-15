using System.Runtime.Versioning;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Platform.MacOS;

[assembly: SupportedOSPlatform("macos")]

internal static class FixturePlatform
{
    internal static IServicePlatform Create() => new ServicePlatform();
    internal static ServicePackageCache OpenCache(string path) => ServicePackageCache.Open(new MacServiceFileSystem(), path);
}
