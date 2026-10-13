using Xunit;
using Xunit.Abstractions;

namespace Darkspyre.Common.Git.Tests;

/// <summary>The one Git version gate: its floor and the Git on PATH.</summary>
public sealed class GitVersionTests(ITestOutputHelper output)
{
    /// <summary>M1: Git 2.34 or newer is available, whatever follows the version number; Git 2.33 is not.</summary>
    [Theory]
    [InlineData("git version 2.33.8\n", false, "Git 2.33.8 is installed; the Git tools need Git 2.34 or newer.")]
    [InlineData("git version 2.34.1\n", true, null)]
    [InlineData("git version 2.43.0\n", true, null)]
    [InlineData("git version 2.50.1 (Apple Git-155)\n", true, null)]
    [InlineData("nonsense", false, "Git returned an unrecognized version.")]
    public void VersionParseAcceptsTwoThirtyFourAndRefusesTwoThirtyThree(string version, bool available, string? diagnostic)
    {
        Assert.Equal(new GitCapabilities(available, diagnostic), GitVersion.Parse(version));
    }

    /// <summary>M14: the Git on PATH is at the floor or newer; the test output names the version under test.</summary>
    [Fact]
    public async Task TheInstalledGitIsAtTheFloorOrNewer()
    {
        string folder = TestScratchRoot.CreateDirectory("git-version-");

        GitCapabilities capabilities = await GitVersion.ReadAsync(new SystemGitProcessLauncher(), folder, default);

        output.WriteLine(Git.Run(folder, "--version").TrimEnd('\n'));
        Assert.True(capabilities.IsAvailable, capabilities.Diagnostic);
    }
}
