using System.Runtime.Versioning;
using Darkspyre.Common.Tests.Support;
using Darkspyre.LocalServices.Platform.MacOS;
using Xunit;

namespace Darkspyre.LocalServices.Platform.MacOS.Tests;

/// <summary>
/// A private directory under the granted application temporary folder opens and is created there.
/// The folder sits under <c>/Volumes</c>, which an app session cannot open on its own.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPrivateDirectoryTests
{
    /// <summary>
    /// Creating a missing private directory and opening that directory both succeed. Opening a directory
    /// that is not there returns without creating it.
    /// </summary>
    [Fact]
    public void GrantedTemporaryFolderOpensExistingAndCreatesMissingPrivateDirectories()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => MacPrivateDirectory.OpenOrCreate(ProbePath()));
            return;
        }

        string leaf = Path.Combine(TemporaryRoot(), "dsa-1291-mac-private-" + Guid.NewGuid().ToString("N"), "service");
        string absent = Path.Combine(leaf, "absent");
        try
        {
            using MacPrivateDirectory created = MacPrivateDirectory.OpenOrCreate(leaf);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(leaf));

            using MacPrivateDirectory opened = MacPrivateDirectory.OpenExisting(leaf)
                ?? throw new InvalidOperationException("The private directory is missing after it was created.");
            Assert.Equal((created.Device, created.Inode), (opened.Device, opened.Inode));

            using MacPrivateDirectory again = MacPrivateDirectory.OpenOrCreate(leaf);
            Assert.Equal((created.Device, created.Inode), (again.Device, again.Inode));

            Assert.Null(MacPrivateDirectory.OpenExisting(absent));
            Assert.False(Directory.Exists(absent));
        }
        finally
        {
            string? createdRoot = Path.GetDirectoryName(leaf);
            if (createdRoot is not null && Directory.Exists(createdRoot)) Directory.Delete(createdRoot, recursive: true);
        }
    }

    private static string ProbePath() => "/private/tmp/dsa-1291-platform-guard";

    private static string TemporaryRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("AGENTDS_TEST_SCRATCH") ?? TestDefaultTempLocation.Read();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Set AGENTDS_TEST_SCRATCH, or the default-temp-location setting, to the granted application temporary folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }
}
