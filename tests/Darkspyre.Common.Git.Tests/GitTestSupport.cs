using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Darkspyre.Common.Git.Tests;

/// <summary>
/// A real Git repository in the test scratch root, a path policy whose answers a test can change mid-test, and a reader
/// whose Git children start through a launcher that records every start.
/// </summary>
internal sealed class GitTestRepository : IAsyncDisposable
{
    private GitTestRepository(string prefix, string? objectFormat)
    {
        this.Root = TestScratchRoot.CreateDirectory(prefix);
        this.Path = System.IO.Path.Combine(this.Root, "repository");
        Directory.CreateDirectory(this.Path);
        this.Launcher = new();
        this.Paths = new();
        this.Commands = new(this.Launcher);
        this.Reader = new(this.Commands, this.Paths);
        string[] format = objectFormat is null ? [] : ["--object-format=" + objectFormat];
        Git.Run(this.Root, ["init", "-q", "-b", "main", .. format, this.Path]);
    }

    public string Root { get; }
    public string Path { get; }
    /// <summary>The launcher every reader child starts through, recording each start.</summary>
    public RecordingLauncher Launcher { get; }
    public TestPathPolicy Paths { get; }
    public GitCommands Commands { get; }
    public GitRepositoryReader Reader { get; }

    public static GitTestRepository Create(string prefix, string? objectFormat = null) => new(prefix, objectFormat);

    public string Write(string relative, string content) => this.Write(relative, Encoding.UTF8.GetBytes(content));

    public string Write(string relative, byte[] content)
    {
        string path = System.IO.Path.Combine(this.Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string Run(params string[] arguments) => Git.Run(this.Path, arguments);

    public string Commit(string message = "Change")
    {
        this.Run("add", "-A");
        this.Run("commit", "-q", "--allow-empty", "-m", message);
        return this.Run("rev-parse", "HEAD").TrimEnd('\n');
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(this.Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runs Git for fixture setup, with a fixed identity and no signing, outside the code under test.</summary>
internal static class Git
{
    public static string Run(string directory, params string[] arguments) => Run(directory, null, arguments);

    public static string Run(string directory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string argument in (string[])["-c", "user.name=Source Test", "-c", "user.email=source@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", "-c", "core.autocrlf=false", .. arguments])
            start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach ((string key, string value) in environment) start.Environment[key] = value;
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {errors.Result}");
        return output.Result;
    }

    /// <summary>Runs a shell script in <paramref name="directory"/>, for setup that needs raw bytes in arguments.</summary>
    public static string Shell(string directory, string script)
    {
        ProcessStartInfo start = new("/bin/sh") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Setup script failed: {errors.Result}");
        return output.Result;
    }
}

/// <summary>
/// The library's path policy over <see cref="UnrestrictedGitPathPolicy"/>. A test can refuse single paths, every read
/// check from a given check on, or every read outside one directory; it counts every read check.
/// </summary>
internal sealed class TestPathPolicy : IGitPathPolicy
{
    private int checks;
    private string? readableRoot;

    /// <summary>Refuses a path, by its absolute lexical form, when it returns true.</summary>
    public Func<string, bool>? Refuse { get; set; }
    /// <summary>Refuses every read check from this check number on, counting from 1.</summary>
    public int? RefuseFromCheck { get; set; }
    public int Checks => Volatile.Read(ref this.checks);

    /// <summary>Permits reads beneath <paramref name="directory"/> only.</summary>
    public void ReadOnlyBeneath(string directory) => this.readableRoot = this.Resolve(directory);

    public string Resolve(string path) => UnrestrictedGitPathPolicy.Instance.Resolve(path);

    public ValueTask<GitPathAccess> GetReadAccessAsync(string absolutePath, CancellationToken cancellationToken)
    {
        int check = Interlocked.Increment(ref this.checks);
        if (this.RefuseFromCheck is { } from && check >= from || this.Refuse?.Invoke(Path.GetFullPath(absolutePath)) == true)
            return ValueTask.FromResult(GitPathAccess.Refused);
        if (this.readableRoot is { } root && !Beneath(root, this.Resolve(absolutePath)))
            return ValueTask.FromResult(GitPathAccess.Refused);
        return UnrestrictedGitPathPolicy.Instance.GetReadAccessAsync(absolutePath, cancellationToken);
    }

    public ValueTask RequireWriteAsync(IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken) =>
        UnrestrictedGitPathPolicy.Instance.RequireWriteAsync(absolutePaths, cancellationToken);

    public ValueTask<string> GetCloneWorkingDirectoryAsync(string destination, CancellationToken cancellationToken) =>
        UnrestrictedGitPathPolicy.Instance.GetCloneWorkingDirectoryAsync(destination, cancellationToken);

    private static bool Beneath(string root, string path) =>
        path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

/// <summary>A launcher over <see cref="SystemGitProcessLauncher"/> that records every start.</summary>
internal sealed class RecordingLauncher : IGitProcessLauncher
{
    private readonly SystemGitProcessLauncher inner = new();
    private readonly ConcurrentQueue<GitProcessStart> started = new();

    public IReadOnlyList<GitProcessStart> Requests => [.. this.started];

    /// <summary>The number of starts whose arguments hold <paramref name="command"/>, such as <c>cat-file</c>.</summary>
    public int Starts(string command) => this.started.Count(start => start.Arguments.Contains(command));

    public async Task<IGitProcess> StartAsync(GitProcessStart start, CancellationToken cancellationToken)
    {
        IGitProcess process = await this.inner.StartAsync(start, cancellationToken);
        this.started.Enqueue(start);
        return process;
    }
}
