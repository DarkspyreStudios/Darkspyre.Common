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
}
