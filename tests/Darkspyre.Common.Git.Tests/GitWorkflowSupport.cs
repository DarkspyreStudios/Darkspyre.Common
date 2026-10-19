using System.Diagnostics;
using System.Text;
using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// A real working repository with a bare remote named <c>origin</c> in the test scratch root, one path policy, one set of
/// repository locks, and a conversation for each caller: its own recording launcher and the library's Git pieces over it.
/// </summary>
internal sealed class GitWorkflowRepository : IAsyncDisposable
{
    private GitWorkflowRepository(string prefix)
    {
        this.Root = TestScratchRoot.CreateDirectory(prefix);
        this.Path = System.IO.Path.Combine(this.Root, "work");
        this.Remote = System.IO.Path.Combine(this.Root, "remote.git");
        Git.Run(this.Root, "init", "-q", "-b", "main", "--bare", this.Remote);
        Git.Run(this.Root, "init", "-q", "-b", "main", this.Path);
        // The host's rebase and cherry-pick commit as the repository's own identity.
        this.Run("config", "user.name", "Source Test");
        this.Run("config", "user.email", "source@example.invalid");
        this.Run("config", "commit.gpgsign", "false");
        this.Write("a.txt", "base\n");
        this.Commit("Base");
        this.Run("remote", "add", "origin", this.Remote);
        this.Run("push", "-q", "-u", "origin", "main");
        this.First = this.Open();
    }

    public string Root { get; }
    /// <summary>The working repository's root.</summary>
    public string Path { get; }
    /// <summary>The bare remote's directory.</summary>
    public string Remote { get; }
    public TestPathPolicy Paths { get; } = new();
    public GitRepositoryLocks Locks { get; } = new();
    public GitConversation First { get; }

    public static GitWorkflowRepository Create(string prefix) => new(prefix);

    /// <summary>A conversation with its own launcher over the same repository, path policy and locks.</summary>
    public GitConversation Open()
    {
        return new(this.Paths, this.Locks);
    }

    public void Write(string relative, string content)
    {
        string path = System.IO.Path.Combine(this.Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Run(params string[] arguments) => Git.Run(this.Path, arguments);

    /// <summary>Runs Git in the working repository and returns its exit code, for a command expected to fail, such as a conflict.</summary>
    public int TryRun(params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = this.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["GIT_EDITOR"] = ":";
        foreach (string argument in (string[])["-c", "user.name=Source Test", "-c", "user.email=source@example.invalid",
            "-c", "commit.gpgsign=false", .. arguments])
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, errors);
        return process.ExitCode;
    }

    public string Commit(string message)
    {
        this.Run("add", "-A");
        this.Run("commit", "-q", "--allow-empty", "-m", message);
        return this.Head();
    }

    public string Head(string reference = "HEAD") => this.Run("rev-parse", reference).TrimEnd('\n');

    public string RemoteHead(string reference) => Git.Run(this.Remote, "rev-parse", reference).TrimEnd('\n');

    /// <summary>The paths the index holds that differ from HEAD.</summary>
    public string[] Staged() => this.Run("diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public ValueTask DisposeAsync()
    {
        // Git writes its objects read-only, and Windows refuses to delete a read-only file.
        foreach (string file in Directory.EnumerateFiles(this.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(this.Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One caller of the library's Git pieces: a launcher that records each Git child it starts, and the reader, commits,
/// transfers, sequences and local changes over it.
/// </summary>
internal sealed class GitConversation
{
    public GitConversation(IGitPathPolicy paths, GitRepositoryLocks locks)
    {
        this.Launcher = new();
        this.Commands = new(this.Launcher);
        this.Reader = new(this.Commands, paths);
        this.Commits = new(this.Commands, this.Reader, locks, paths);
        this.Transfers = new(this.Commands, this.Reader, locks, paths);
        this.Sequences = new(this.Commands, this.Reader, locks, paths);
        this.Local = new(this.Commands, this.Reader, locks, paths);
        this.Worktrees = new(this.Commands, this.Reader, locks, paths);
    }

    /// <summary>Every Git child this conversation starts, recorded with its command.</summary>
    public RecordingLauncher Launcher { get; }
    public GitCommands Commands { get; }
    public GitRepositoryReader Reader { get; }
    public GitCommits Commits { get; }
    public GitTransfers Transfers { get; }
    public GitSequences Sequences { get; }
    public GitLocalChanges Local { get; }
    public GitWorktrees Worktrees { get; }

    public Task<GitStatusSnapshot?> StatusAsync(string repository) => this.Reader.ReadStatusAsync(repository, default);

    /// <summary>Waits until this conversation has started <paramref name="count"/> Git children running <paramref name="command"/>.</summary>
    public async Task StartedAsync(string command, int count = 1)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (this.Launcher.Starts(command) < count)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), $"No {command} child started.");
            await Task.Delay(20);
        }
    }
}

/// <summary>Writes executable shell scripts for remote fixtures, such as a hook or an upload-pack wrapper.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class GitScripts
{
    public static string Write(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
