using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// The home directory every Git child the library starts runs with, so Git reads the configuration and SSH settings of
/// the account it runs as rather than whatever home the environment block it inherited happens to name (DSA-1281).
/// </summary>
public sealed class GitEnvironmentTests
{
    /// <summary>Every Git child, read or change, names the account's own home directory in <c>HOME</c>.</summary>
    [Fact]
    public async Task EveryGitChildNamesTheAccountsOwnHome()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-home-");
        repository.Write("a.txt", "base\n");
        repository.Commit();

        _ = await repository.Reader.ReadStatusAsync(repository.Path, default);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEmpty(home);
        Assert.NotEmpty(repository.Launcher.Requests);
        Assert.All(repository.Launcher.Requests, start => Assert.Equal(home, start.Environment["HOME"]));
    }

    /// <summary>
    /// A home rooted at a drive letter also names <c>HOMEDRIVE</c> and <c>HOMEPATH</c>, which Git for Windows prefers
    /// over <c>USERPROFILE</c> and its own shell and ssh recompute the home from; any other home names only <c>HOME</c>.
    /// </summary>
    [Fact]
    public void AHomeRootedAtADriveLetterAlsoNamesHomeDriveAndHomePath()
    {
        const string service = @"C:\Windows\System32\config\systemprofile";

        Assert.Equal([new("HOME", service), new("HOMEDRIVE", "C:"),
            new KeyValuePair<string, string?>("HOMEPATH", @"\Windows\System32\config\systemprofile")],
            GitCommands.HomeEntries(service));
        Assert.Equal([new KeyValuePair<string, string?>("HOME", "/Users/person")], GitCommands.HomeEntries("/Users/person"));
        Assert.Empty(GitCommands.HomeEntries(""));
    }
}
