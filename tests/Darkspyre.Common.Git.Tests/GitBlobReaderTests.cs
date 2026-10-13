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

    /// <summary>T18: a header over the reader's own bound stops it without reading the body; the next read is refused.</summary>
    [Fact]
    public async Task AnOversizedHeaderStopsTheReaderWithoutReadingTheBody()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-blob-limit-");
        repository.Write("a.txt", "ten bytes\n");
        repository.Commit();
        string a = repository.Run("rev-parse", "HEAD:a.txt").TrimEnd('\n');

        await using GitBlobReader reader = await GitBlobReader.StartAsync(repository.Commands, repository.Path,
            GitObjectFormat.Sha1, default);
        GitReadException limited = await Assert.ThrowsAsync<GitReadException>(() => reader.ReadAsync(a, 4, default));
        IOException stopped = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(a, 1024, default));

        Assert.Equal(GitReadFailure.LimitExceeded, limited.Failure);
        Assert.Equal("The object reader stopped after an earlier failure.", stopped.Message);
    }

    /// <summary>
    /// A read child runs with the read options and clears every trace and config variable the process inherited, including
    /// names only the process's environment carries.
    /// </summary>
    [Fact]
    public async Task AReadChildClearsTheInheritedTraceAndConfigVariables()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-blob-environment-");
        repository.Write("a.txt", "first\n");
        repository.Commit();
        string a = repository.Run("rev-parse", "HEAD:a.txt").TrimEnd('\n');
        // Names Git ignores, so another test's Git child that inherits them behaves the same.
        const string trace = "GIT_TRACE_DSA957_TEST", key = "GIT_CONFIG_KEY_957", value = "GIT_CONFIG_VALUE_957";
        Environment.SetEnvironmentVariable(trace, "1");
        Environment.SetEnvironmentVariable(key, "core.pager");
        Environment.SetEnvironmentVariable(value, "cat");
        try
        {
            await using GitBlobReader reader = await GitBlobReader.StartAsync(repository.Commands, repository.Path,
                GitObjectFormat.Sha1, default);
            Assert.Equal("first\n"u8.ToArray(), await reader.ReadAsync(a, 1024, default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(trace, null);
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(value, null);
        }

        // The partial-clone check starts a config child first; the repository is a full clone, so no rev-list starts.
        GitProcessStart request = Assert.Single(repository.Launcher.Requests, start => start.Arguments.Contains("cat-file"));
        Assert.Equal(0, repository.Launcher.Starts("rev-list"));
        IReadOnlyDictionary<string, string?> environment = request.Environment;
        foreach (string cleared in (string[])[trace, key, value, "GIT_TRACE", "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE_PACKET",
            "GIT_CONFIG_COUNT", "GIT_NAMESPACE"])
            Assert.True(environment.TryGetValue(cleared, out string? set) && set is null, $"{cleared} was not cleared.");
        Assert.Equal("1", environment["GIT_NO_REPLACE_OBJECTS"]);
        Assert.Subset(request.Arguments.ToHashSet(), new HashSet<string> { "--no-replace-objects", "--no-optional-locks" });
    }

    /// <summary>M8: no read child carries an option or a variable that needs a Git newer than 2.34.</summary>
    [Fact]
    public async Task ReadsRunWithoutLazyFetchOptions()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-read-options-");
        repository.Write("a.txt", "first\n");
        string head = repository.Commit();

        await repository.Reader.InspectAsync(repository.Path, default);
        await using GitFileContent content = await repository.Reader.ReadFileAtCommitAsync(new(repository.Path, head, "a.txt", 1024), default);

        Assert.NotEmpty(repository.Launcher.Requests);
        foreach (GitProcessStart start in repository.Launcher.Requests)
        {
            Assert.Equal(GitProcessKind.Read, start.Kind);
            Assert.DoesNotContain("--no-lazy-fetch", start.Arguments);
            Assert.False(start.Environment.ContainsKey("GIT_NO_LAZY_FETCH"), "A read set GIT_NO_LAZY_FETCH.");
        }
    }

    /// <summary>
    /// M9: in a promisor-remote clone a missing blob is reported and not fetched, through one config and one rev-list
    /// child; in a full clone two reads through one reader start one config child and no rev-list.
    /// </summary>
    [Fact]
    public async Task ABlobMissingFromAPartialCloneIsReportedAndNotFetched()
    {
        await using GitTestRepository source = GitTestRepository.Create("git-partial-source-");
        source.Write("a.txt", "first\n");
        source.Write("b.txt", "second\n");
        source.Commit();
        source.Run("config", "uploadpack.allowFilter", "true");
        string a = source.Run("rev-parse", "HEAD:a.txt").TrimEnd('\n');
        string b = source.Run("rev-parse", "HEAD:b.txt").TrimEnd('\n');

        string clone = Path.Combine(source.Root, "partial");
        Git.Run(source.Root, "clone", "-q", "--no-checkout", "--filter=blob:none", "file://" + source.Path, clone);
        Assert.Contains("remote.origin.promisor true", Git.Run(clone, "config", "--get-regexp", "promisor"), StringComparison.Ordinal);
        RecordingLauncher launcher = new();
        GitCommands commands = new(launcher);
        await using (GitBlobReader reader = await GitBlobReader.StartAsync(commands, clone, GitObjectFormat.Sha1, default))
        {
            GitReadException missing = await Assert.ThrowsAsync<GitReadException>(() => reader.ReadAsync(a, 1024, default));
            Assert.Equal(GitReadFailure.ObjectMissing, missing.Failure);
        }
        Assert.Equal(1, launcher.Starts("config"));
        Assert.Equal(1, launcher.Starts("rev-list"));
        // With the remote gone, a lazy fetch cannot succeed, so cat-file -e reports only what the clone holds.
        Git.Run(clone, "config", "remote.origin.url", Path.Combine(source.Root, "gone"));
        Assert.Equal("absent\n", Git.Shell(clone, $"if git cat-file -e {a} 2>/dev/null; then echo present; else echo absent; fi"));

        await using GitBlobReader full = await GitBlobReader.StartAsync(source.Commands, source.Path, GitObjectFormat.Sha1, default);
        Assert.Equal("first\n"u8.ToArray(), await full.ReadAsync(a, 1024, default));
        Assert.Equal("second\n"u8.ToArray(), await full.ReadAsync(b, 1024, default));
        Assert.Equal(1, source.Launcher.Starts("config"));
        Assert.Equal(0, source.Launcher.Starts("rev-list"));
    }
}
