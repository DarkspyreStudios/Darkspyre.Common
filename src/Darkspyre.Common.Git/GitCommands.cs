using System.Collections;
using System.Collections.Immutable;

namespace Darkspyre.Common.Git;

/// <summary>Raw Git transport over the caller's process launcher, never a shell or a second launcher.</summary>
internal sealed class GitCommands(IGitProcessLauncher processes, GitReadPolicy readPolicy = GitReadPolicy.Configured)
{
    private readonly IGitProcessLauncher processes = processes;
    internal GitReadPolicy ReadPolicy { get; } = readPolicy;

    // Git credential helpers/configuration remain Git-owned. Ambient repository overrides must not
    // redirect a scoped operation to another index, object directory or working tree.
    private static readonly ImmutableDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_COMMON_DIR"] = null,
        ["GIT_INDEX_FILE"] = null, ["GIT_OBJECT_DIRECTORY"] = null, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
        ["GIT_PREFIX"] = null, ["GIT_CONFIG_COUNT"] = null, ["GIT_CONFIG_PARAMETERS"] = null,
        ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    // A read also never follows a replacement ref, and it clears the inherited namespace and every trace variable, so Git
    // copies no output anywhere but its own pipes.
    private static readonly ImmutableDictionary<string, string?> ReadEnvironment = Environment.SetItems(new Dictionary<string, string?>
    {
        ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_NAMESPACE"] = null,
        ["GIT_TRACE"] = null, ["GIT_TRACE_SETUP"] = null, ["GIT_TRACE_PACKET"] = null, ["GIT_TRACE_PERFORMANCE"] = null,
        ["GIT_TRACE_CURL"] = null, ["GIT_CURL_VERBOSE"] = null, ["GIT_TRACE2"] = null, ["GIT_TRACE2_EVENT"] = null,
        ["GIT_TRACE2_PERF"] = null,
    });

    /// <summary>The global options every read child runs with, before its command.</summary>
    internal static readonly ImmutableArray<string> ReadOptions = ["--no-replace-objects", "--no-optional-locks"];

    // Git treats the editor ":" as "keep the message": a rebase or cherry-pick never waits on an editor.
    private static readonly ImmutableDictionary<string, string?> SequenceEnvironment = Environment.SetItems(new Dictionary<string, string?>
    {
        ["GIT_EDITOR"] = ":", ["GIT_SEQUENCE_EDITOR"] = ":",
    });

    internal Task<GitRunResult> RunAsync(string directory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        this.RunCoreAsync(directory, arguments, new(Environment, GitProcessKind.Change, new()), cancellationToken);

    /// <summary>
    /// Runs one read-only command with the read options and the read environment, as a <see cref="GitProcessKind.Read"/>
    /// child. With <paramref name="timeout"/>, a child that runs longer is stopped and the result reports
    /// <see cref="GitRunResult.TimedOut"/>. <paramref name="input"/> is written to its standard input, which is then
    /// closed.
    /// </summary>
    internal Task<GitRunResult> RunReadAsync(string directory, IReadOnlyList<string> arguments,
        TimeSpan? timeout = null, ReadOnlyMemory<byte> input = default, CancellationToken cancellationToken = default) =>
        this.RunCoreAsync(directory, [.. ReadOptions, .. arguments],
            new(this.CurrentReadEnvironment(), GitProcessKind.Read, new() { Timeout = timeout, Input = input }), cancellationToken);

    /// <summary>
    /// Runs one rebase or cherry-pick command with <c>GIT_EDITOR</c> and <c>GIT_SEQUENCE_EDITOR</c> set to <c>:</c>, so Git
    /// keeps every message and never starts an editor. It is a <see cref="GitProcessKind.Change"/> child, as every mutation.
    /// </summary>
    internal Task<GitRunResult> RunSequenceAsync(string directory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        this.RunCoreAsync(directory, arguments, new(SequenceEnvironment, GitProcessKind.Change, new()), cancellationToken);

    /// <summary>
    /// Starts one long-lived <see cref="GitProcessKind.Read"/> child with the read options and the read environment. The
    /// caller owns the process and reads both of its streams.
    /// </summary>
    internal Task<IGitProcess> StartReadAsync(string directory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GitProcessStart start = new(["--no-pager", "-c", "core.fsmonitor=false", .. ReadOptions, .. arguments],
            directory, this.CurrentReadEnvironment(), GitProcessKind.Read);
        return this.processes.StartAsync(start, cancellationToken);
    }

    private async Task<GitRunResult> RunCoreAsync(string directory, IReadOnlyList<string> arguments, RunOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Capture.Timeout is { } timeout) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        // Explicit argv preserves whitespace, NUL-delimited Git output and shell-looking path data.
        GitProcessStart start = new(["--no-pager", "-c", "core.fsmonitor=false", .. arguments], directory,
            options.Environment, options.Kind);
        IGitProcess process = await this.processes.StartAsync(start, cancellationToken).ConfigureAwait(false);
        await using (process.ConfigureAwait(false))
            return await GitCapture.CaptureAsync(process, options.Capture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How one run-to-completion child starts and is captured.</summary>
    private sealed record RunOptions(ImmutableDictionary<string, string?> Environment, GitProcessKind Kind, GitRunOptions Capture);

    // GIT_CONFIG_KEY_n, GIT_CONFIG_VALUE_n and any other GIT_TRACE* name exist only as the host inherited them, so they are
    // read from the host's environment at each start and cleared.
    private ImmutableDictionary<string, string?> CurrentReadEnvironment()
    {
        ImmutableDictionary<string, string?> environment = ReadEnvironment;
        foreach (DictionaryEntry inherited in System.Environment.GetEnvironmentVariables())
            if (inherited.Key is string name && (name.StartsWith("GIT_TRACE", StringComparison.Ordinal)
                || name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)))
                environment = environment.SetItem(name, null);
        // Git 2.34 supports this transport allowlist. An empty list also forbids implicit file/remote-helper fetches.
        if (this.ReadPolicy == GitReadPolicy.LocalOnly) environment = environment.SetItem("GIT_ALLOW_PROTOCOL", "");
        return environment;
    }
}
