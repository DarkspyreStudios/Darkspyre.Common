using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>DSA-952 3.3.1: one plain <c>git cat-file --batch</c> child serves a call's blob reads.</summary>
public sealed class GitBlobReaderTests
{
    /// <summary>T17: two blobs and a missing object through one child; the read after the missing object succeeds.</summary>
    [Fact]
    public async Task OneReaderServesSuccessiveBlobsAndAMissingObjectKeepsItUsable()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-blob-reader-");
        repository.Write("a.txt", "first\n");
        repository.Write("b.txt", "second blob\n");
        repository.Commit();
        string a = repository.Run("rev-parse", "HEAD:a.txt").TrimEnd('\n');
        string b = repository.Run("rev-parse", "HEAD:b.txt").TrimEnd('\n');

        await using GitBlobReader reader = await GitBlobReader.StartAsync(repository.Commands, repository.Path,
            GitObjectFormat.Sha1, default);
        byte[] first = await reader.ReadAsync(a, 1024, default);
        byte[] second = await reader.ReadAsync(b, 1024, default);
        GitReadException missing = await Assert.ThrowsAsync<GitReadException>(() => reader.ReadAsync(new string('0', 40), 1024, default));
        byte[] again = await reader.ReadAsync(a, 1024, default);

        Assert.Equal("first\n"u8.ToArray(), first);
        Assert.Equal("second blob\n"u8.ToArray(), second);
        Assert.Equal(GitReadFailure.ObjectMissing, missing.Failure);
        Assert.Equal("The object is not in the local object store; nothing was fetched.", missing.Message);
        Assert.Equal(first, again);
        Assert.Equal(1, repository.Launcher.Starts("cat-file"));
    }

    /// <summary>In a promisor-remote clone a missing blob is reported and not fetched.</summary>
    [Fact]
    public async Task ABlobMissingFromAPartialCloneIsReportedAndNotFetched()
    {
        await using GitTestRepository source = GitTestRepository.Create("git-partial-source-");
        source.Write("a.txt", "first\n");
        source.Commit();
        source.Run("config", "uploadpack.allowFilter", "true");
        string a = source.Run("rev-parse", "HEAD:a.txt").TrimEnd('\n');

        string clone = Path.Combine(source.Root, "partial");
        Git.Run(source.Root, "clone", "-q", "--no-checkout", "--filter=blob:none", "file://" + source.Path, clone);
        await using (GitBlobReader reader = await GitBlobReader.StartAsync(new(new RecordingLauncher()), clone, GitObjectFormat.Sha1, default))
        {
            GitReadException missing = await Assert.ThrowsAsync<GitReadException>(() => reader.ReadAsync(a, 1024, default));
            Assert.Equal(GitReadFailure.ObjectMissing, missing.Failure);
        }
        // With the remote gone, a lazy fetch cannot succeed, so cat-file -e reports only what the clone holds.
        Git.Run(clone, "config", "remote.origin.url", Path.Combine(source.Root, "gone"));
        Assert.Equal("absent\n", Git.Shell(clone, $"if git cat-file -e {a} 2>/dev/null; then echo present; else echo absent; fi"));
    }
}
