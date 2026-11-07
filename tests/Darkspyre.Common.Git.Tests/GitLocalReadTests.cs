using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>Transport restrictions keep partial-clone reads local without changing the default client.</summary>
public sealed class GitLocalReadTests
{
    /// <summary>A local-only tree read of a partial clone fails and fetches nothing, whether the blobs or the trees are missing.</summary>
    [Theory]
    [InlineData("blob:none")]
    public async Task LocalOnlyTreeReadsOfAPartialCloneFetchNothing(string filter)
    {
        await using GitTestRepository source = GitTestRepository.Create("git-local-tree");
        source.Write("folder/a.txt", "committed\n");
        string commit = source.Commit();
        source.Run("config", "uploadpack.allowFilter", "true");
        string missing = source.Run("rev-parse", filter == "tree:0" ? "HEAD^{tree}" : "HEAD:folder/a.txt").TrimEnd('\n');
        string clone = Path.Combine(source.Root, "partial");
        Git.Run(source.Root, "clone", "-q", "--no-checkout", "--filter=" + filter, "file://" + source.Path, clone);
        // Even an explicit per-protocol permission cannot override the local-only process environment.
        Git.Run(clone, "config", "protocol.file.allow", "always");
        string configuration = await File.ReadAllTextAsync(Path.Combine(clone, ".git", "config"));
        string[] packs = Directory.GetFiles(Path.Combine(clone, ".git", "objects", "pack")).Order(StringComparer.Ordinal).ToArray();
        Assert.False(Present(clone, missing));
        GitClient client = new(new RecordingLauncher(), UnrestrictedGitPathPolicy.Instance, new(), GitReadPolicy.LocalOnly);

        await Assert.ThrowsAnyAsync<IOException>(() => client.ReadTreeAsync(new(clone, commit, "")));

        Assert.False(Present(clone, missing));
        Assert.Equal(packs, Directory.GetFiles(Path.Combine(clone, ".git", "objects", "pack")).Order(StringComparer.Ordinal));
        Assert.Equal(configuration, await File.ReadAllTextAsync(Path.Combine(clone, ".git", "config")));
    }

    private static bool Present(string repository, string objectId) =>
        Git.Run(repository, "rev-list", "--objects", "--no-object-names", "--no-walk", "--ignore-missing", "--missing=print", objectId)
            .Split('\n').Contains(objectId, StringComparer.Ordinal);
}
