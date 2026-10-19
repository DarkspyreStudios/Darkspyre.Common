using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// DSA-952 3.3: a committed file read into memory and a commit's tree listing, through the reader over a real repository,
/// with the caller's path permission checked before Git is asked about a path.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GitFileReadTests
{
    /// <summary>T8: the file's exact bytes and mode at the commit; an absent path is not present and has no content.</summary>
    [Fact]
    public async Task AFileAtACommitReadsItsBytesAndAnAbsentPathIsNotPresent()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-file-read-");
        repository.Write("notes/a.txt", "committed\nbytes\n");
        string commit = repository.Commit();
        repository.Write("notes/a.txt", "a local edit\n");

        await using GitFileContent present = await repository.Reader.ReadFileAtCommitAsync(new(repository.Path, "main", "notes/a.txt", 1024), default);
        await using GitFileContent absent = await repository.Reader.ReadFileAtCommitAsync(new(repository.Path, "main", "notes/missing.txt", 1024), default);

        Assert.Equal((commit, true, "100644"), (present.CommitId, present.Present, present.Mode));
        using MemoryStream bytes = new();
        await present.Content!.CopyToAsync(bytes);
        Assert.Equal("committed\nbytes\n", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.False(present.Content.CanWrite);
        Assert.Equal((commit, false, null, null), (absent.CommitId, absent.Present, absent.Mode, absent.Content));
    }

    /// <summary>
    /// M10: the listing carries no size, so a file over the limit fails from the cat-file header with nothing of its body
    /// read.
    /// </summary>
    [Fact]
    public async Task AFileAtCommitOverTheLimitFailsFromTheBatchHeader()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-file-limit-");
        repository.Write("large.txt", "ten bytes\n");
        repository.Commit();

        GitReadException large = await Assert.ThrowsAsync<GitReadException>(() =>
            repository.Reader.ReadFileAtCommitAsync(new(repository.Path, "HEAD", "large.txt", 4), default));

        Assert.Equal(GitReadFailure.LimitExceeded, large.Failure);
        Assert.Equal("The committed file is larger than the limit; nothing was read.", large.Message);
    }

    /// <summary>T10: a path the path policy refuses is refused before any Git child asks about it.</summary>
    [Fact]
    public async Task ARefusedPathStartsNoGitChild()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-file-refused-");
        repository.Write("secret.txt", "secret\n");
        repository.Commit();
        repository.Paths.Refuse = path => path.EndsWith("secret.txt", StringComparison.Ordinal);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.Reader.ReadFileAtCommitAsync(new(repository.Path, "HEAD", "secret.txt", 1024), default));

        // Locating the working tree runs four rev-parse children; nothing resolves the reference or lists the path.
        Assert.Equal(4, repository.Launcher.Starts("rev-parse"));
        Assert.Equal(0, repository.Launcher.Starts("ls-tree"));
        Assert.Equal(0, repository.Launcher.Starts("cat-file"));
    }

    /// <summary>T11: every leaf's kind, mode, object ID and length, in Git's order, without the paths the path policy refuses.</summary>
    [Fact]
    public async Task ATreeListsLeavesWithKindModeIdAndLengthAndLeavesOutRefusedPaths()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-tree-read-");
        repository.Write("a.txt", "a\n");
        repository.Write("dir/b.txt", "bee\n");
        repository.Write("run.sh", "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(repository.Path, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(repository.Path, "link"), "a.txt");
        repository.Write("secret/hidden.txt", "hidden\n");
        string first = repository.Commit("Files");
        repository.Run("update-index", "--add", "--cacheinfo", "160000," + first + ",module");
        repository.Run("commit", "-q", "-m", "Submodule");
        string commit = repository.Run("rev-parse", "HEAD").TrimEnd('\n');
        repository.Paths.Refuse = path => path.Contains("/secret/", StringComparison.Ordinal);

        IReadOnlyList<GitTreeEntry> all = await repository.Reader.ReadTreeAsync(new(repository.Path, commit, ""), default);
        IReadOnlyList<GitTreeEntry> folder = await repository.Reader.ReadTreeAsync(new(repository.Path, commit, "dir"), default);

        Assert.Equal(["a.txt", "dir/b.txt", "link", "module", "run.sh"], all.Select(entry => entry.Path));
        Assert.Equal(new GitTreeEntry("a.txt", GitEntryKind.RegularFile, "100644", repository.Run("rev-parse", commit + ":a.txt").TrimEnd('\n'), 2),
            all[0]);
        Assert.Equal((GitEntryKind.SymbolicLink, "120000", 5L), (all[2].Kind, all[2].Mode, all[2].Length!.Value));
        Assert.Equal(new GitTreeEntry("module", GitEntryKind.GitLink, "160000", first, null), all[3]);
        Assert.Equal((GitEntryKind.ExecutableFile, "100755"), (all[4].Kind, all[4].Mode));
        Assert.Equal("dir/b.txt", Assert.Single(folder).Path);
        Assert.Equal(4, folder[0].Length);
    }
}
