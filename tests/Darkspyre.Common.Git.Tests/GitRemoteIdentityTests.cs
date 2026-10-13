using System.Text.Json;
using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// Remote metadata reads configured remotes through Git's own URL rewriting and returns structured endpoints only: no raw
/// URL, user name, password or query reaches a result, an exception or process replay. Real repositories hold the
/// configuration; synthetic sentinel secrets prove nothing leaks, without printing them on failure.
/// </summary>
public sealed class GitRemoteIdentityTests
{
    [Fact]
    public async Task HttpCredentialsQueriesAndFragmentsNeverLeave()
    {
        string secret = "secret-" + Guid.NewGuid().ToString("N");
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-http-");
        repository.Run("config", "remote.origin.url", $"https://user:{secret}@Example.INVALID:8443/Org/Repo.git");
        repository.Run("config", "--add", "remote.origin.url", $"https://example.invalid/signed.git?token={secret}");
        repository.Run("config", "--add", "remote.origin.url", $"https://example.invalid/anchored.git#{secret}");
        repository.Run("config", "--add", "remote.origin.url", "https://example.invalid:443/Explicit/Default%20Port.git");
        repository.Run("config", "--add", "remote.origin.url", "http://bücher.invalid/a/../b.git");

        GitRemoteInfo origin = Assert.Single(await ReadAsync(repository));

        Assert.Equal("origin", origin.Name);
        Assert.Equal(
        [
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "example.invalid", 8443, "/Org/Repo.git"),
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.RequiresAssociation, null, null, null),
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.RequiresAssociation, null, null, null),
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "example.invalid", 443, "/Explicit/Default%20Port.git"),
            new GitRemoteEndpoint(GitRemoteTransport.Http, GitRemoteIdentityStatus.Parsed, "xn--bcher-kva.invalid", null, "/a/../b.git"),
        ], origin.Fetch);
        Assert.Equal(origin.Fetch, origin.Push);
        AssertNoLeak(repository, secret, origin);
    }

    [Fact]
    public async Task SshAndScpKeepTheirPathsWhileOtherUsersAndHelpersRequireAssociation()
    {
        string user = "user" + Guid.NewGuid().ToString("N")[..8];
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-ssh-");
        repository.Run("config", "remote.ssh.url", "ssh://git@Host.INVALID:2222/srv/repo.git");
        repository.Run("config", "--add", "remote.ssh.url", "ssh://host.invalid/~team/repo.git");
        repository.Run("config", "--add", "remote.ssh.url", $"ssh://{user}@host.invalid/srv/repo.git");
        repository.Run("config", "remote.scp.url", "git@host.invalid:team/repo.git");
        repository.Run("config", "--add", "remote.scp.url", "host.invalid:/srv/repo.git");
        repository.Run("config", "--add", "remote.scp.url", $"{user}@host.invalid:team/repo.git");
        repository.Run("config", "--add", "remote.scp.url", "git@[::1]:repo.git");
        repository.Run("config", "remote.helper.url", $"ext::sh -c {user}");
        repository.Run("config", "--add", "remote.helper.url", "git://host.invalid/repo.git");

        Dictionary<string, GitRemoteInfo> remotes = (await ReadAsync(repository)).ToDictionary(remote => remote.Name);

        Assert.Equal(["ssh", "scp", "helper"], remotes.Keys);
        Assert.Equal(
        [
            new(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.Parsed, "host.invalid", 2222, "/srv/repo.git"),
            new(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.Parsed, "host.invalid", null, "/~team/repo.git"),
            new GitRemoteEndpoint(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.RequiresAssociation, null, null, null),
        ], remotes["ssh"].Fetch);
        Assert.Equal(
        [
            new(GitRemoteTransport.Scp, GitRemoteIdentityStatus.Parsed, "host.invalid", null, "team/repo.git"),
            new(GitRemoteTransport.Scp, GitRemoteIdentityStatus.Parsed, "host.invalid", null, "/srv/repo.git"),
            new(GitRemoteTransport.Scp, GitRemoteIdentityStatus.RequiresAssociation, null, null, null),
            new GitRemoteEndpoint(GitRemoteTransport.Scp, GitRemoteIdentityStatus.Parsed, "::1", null, "repo.git"),
        ], remotes["scp"].Fetch);
        Assert.All(remotes["helper"].Fetch, endpoint => Assert.Equal(GitRemoteEndpoints.Unrecognized, endpoint));
        AssertNoLeak(repository, user, [.. remotes.Values]);
    }

    [Fact]
    public async Task EveryDestinationStaysDistinctThroughRewritesAndPushDisagreement()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-rewrite-");
        repository.Run("config", "url.https://rewritten.invalid/.insteadOf", "short:");
        repository.Run("config", "url.ssh://git@push.invalid/.pushInsteadOf", "short:");
        repository.Run("config", "remote.origin.url", "short:first.git");
        repository.Run("config", "--add", "remote.origin.url", "https://second.invalid/second.git");
        repository.Run("config", "remote.split.url", "https://fetch.invalid/repo.git");
        repository.Run("config", "remote.split.pushurl", "short:pushed.git");
        repository.Run("config", "--add", "remote.split.pushurl", "https://other.invalid/pushed.git");

        Dictionary<string, GitRemoteInfo> remotes = (await ReadAsync(repository)).ToDictionary(remote => remote.Name);

        Assert.Equal(
        [
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "rewritten.invalid", null, "/first.git"),
            new GitRemoteEndpoint(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "second.invalid", null, "/second.git"),
        ], remotes["origin"].Fetch);
        // pushInsteadOf matches only the first configured URL, so the push list is that one rewritten destination.
        Assert.Equal([new GitRemoteEndpoint(GitRemoteTransport.Ssh, GitRemoteIdentityStatus.Parsed, "push.invalid", null, "/first.git")],
            remotes["origin"].Push);
        Assert.Equal([new GitRemoteEndpoint(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "fetch.invalid", null, "/repo.git")],
            remotes["split"].Fetch);
        Assert.Equal(
        [
            new(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "rewritten.invalid", null, "/pushed.git"),
            new GitRemoteEndpoint(GitRemoteTransport.Https, GitRemoteIdentityStatus.Parsed, "other.invalid", null, "/pushed.git"),
        ], remotes["split"].Push);
    }

    [Fact]
    public async Task ALocalDestinationIsDescribedOnlyInsideTheCallersScope()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-local-");
        string inside = Directory.CreateDirectory(Path.Combine(repository.Path, "nested", "peer.git")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(repository.Root, "outside.git")).FullName;
        repository.Paths.ReadOnlyBeneath(repository.Path);
        repository.Run("config", "remote.inside.url", "nested/peer.git");
        repository.Run("config", "remote.outside.url", outside);
        repository.Run("config", "remote.file.url", "file://" + outside);

        Dictionary<string, GitRemoteInfo> remotes = (await ReadAsync(repository)).ToDictionary(remote => remote.Name);

        Assert.Equal([new GitRemoteEndpoint(GitRemoteTransport.File, GitRemoteIdentityStatus.Parsed, null, null, repository.Paths.Resolve(inside))],
            remotes["inside"].Fetch);
        GitRemoteEndpoint refused = new(GitRemoteTransport.File, GitRemoteIdentityStatus.RequiresAssociation, null, null, null);
        Assert.Equal([refused], remotes["outside"].Fetch);
        Assert.Equal([refused], remotes["file"].Fetch);
    }

    [Fact]
    public async Task AConfiguredLineBreakOrUnsafeNameIsNeverEchoed()
    {
        string secret = "secret-" + Guid.NewGuid().ToString("N");
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-unsafe-");
        repository.Run("config", "remote.broken.url", $"https://example.invalid/{secret}\nhttps://second.invalid/x.git");
        repository.Run("config", "--add", "remote.broken.url", "https://third.invalid/y.git");

        GitRemoteInfo broken = Assert.Single(await ReadAsync(repository));

        Assert.Equal([GitRemoteEndpoints.Unrecognized, GitRemoteEndpoints.Unrecognized], broken.Fetch);
        Assert.Equal(broken.Fetch, broken.Push);
        AssertNoLeak(repository, secret, broken);

        string name = "-name" + Guid.NewGuid().ToString("N")[..8];
        repository.Run("config", $"remote.{name}.url", "https://example.invalid/x.git");
        InvalidDataException unsafeName = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(repository));
        Assert.True(!unsafeName.Message.Contains(name, StringComparison.Ordinal), "The refusal echoed the remote's name.");
    }

    [Fact]
    public async Task ARepositoryWithoutRemotesReturnsAnEmptyList()
    {
        await using GitTestRepository repository = GitTestRepository.Create("git-remote-none-");

        Assert.Empty(await ReadAsync(repository));
    }

    private static Task<IReadOnlyList<GitRemoteInfo>> ReadAsync(GitTestRepository repository) =>
        repository.Reader.ReadRemoteEndpointsAsync(repository.Path, default);

    // The result, and every Git child this test started, hold no trace of the secret: each child is a read, whose output a
    // launcher keeps no copy of, and no argument carries the secret.
    private static void AssertNoLeak(GitTestRepository repository, string secret, params GitRemoteInfo[] remotes)
    {
        Assert.True(!JsonSerializer.Serialize(remotes).Contains(secret, StringComparison.Ordinal), "A remote endpoint carried the secret.");
        Assert.NotEmpty(repository.Launcher.Requests);
        foreach (GitProcessStart request in repository.Launcher.Requests)
        {
            Assert.Equal(GitProcessKind.Read, request.Kind);
            Assert.True(!request.Arguments.Any(argument => argument.Contains(secret, StringComparison.Ordinal)),
                "A Git child's arguments carried the secret.");
        }
    }
}
