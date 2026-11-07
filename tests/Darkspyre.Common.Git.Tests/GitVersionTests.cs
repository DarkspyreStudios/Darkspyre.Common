using Xunit;
using Xunit.Abstractions;

namespace Darkspyre.Common.Git.Tests;

/// <summary>The one Git version gate: its floor and the Git on PATH.</summary>
public sealed class GitVersionTests(ITestOutputHelper output)
{
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
