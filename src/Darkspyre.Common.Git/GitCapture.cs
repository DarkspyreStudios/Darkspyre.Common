namespace Darkspyre.Common.Git;

/// <summary>How one Git child runs to completion.</summary>
internal sealed record GitRunOptions
{
    /// <summary>The bytes written to the child's standard input, which is then closed. Empty writes nothing.</summary>
    internal ReadOnlyMemory<byte> Input { get; init; }
    /// <summary>How long the child may run before it is stopped, or null for no limit. When set, it must be positive.</summary>
    internal TimeSpan? Timeout { get; init; }
    /// <summary>
    /// The most bytes kept across standard output and standard error together, or null for no bound. Output past the
    /// bound is read and dropped, so a full pipe never blocks the child.
    /// </summary>
    internal int? OutputLimit { get; init; }
}

/// <summary>A Git child's exit and both output channels.</summary>
/// <param name="Exit">How the child exited.</param>
/// <param name="Output">What the child wrote to standard output, up to the bound.</param>
/// <param name="Errors">What the child wrote to standard error, up to the bound.</param>
/// <param name="TimedOut">True when the timeout stopped the child.</param>
/// <param name="OutputLimitExceeded">True when the child wrote more than <see cref="GitRunOptions.OutputLimit"/>.</param>
internal sealed record GitRunResult(GitExit Exit, byte[] Output, byte[] Errors, bool TimedOut = false,
    bool OutputLimitExceeded = false);

/// <summary>The one run-to-completion path for a Git child: input, both channels and exit together.</summary>
internal static class GitCapture
{
    /// <summary>How long a timed-out child has to exit after a stop request before it is forced to stop.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private const int ChunkSize = 4096;

    /// <summary>
    /// Writes the input, drains both channels and waits for the exit. The caller owns <paramref name="process"/> and
    /// disposes it; disposal stops a child that is still running.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static async Task<GitRunResult> CaptureAsync(IGitProcess process, GitRunOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout is { } positive) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(positive, TimeSpan.Zero);
        if (options.OutputLimit is { } bound) ArgumentOutOfRangeException.ThrowIfNegative(bound);
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        Bound limit = new(options.OutputLimit, output, errors);
        Task writing = WriteAsync(process.StandardInput, options.Input, capture);
        Task stdout = DrainAsync(process.StandardOutput, output, limit, capture);
        Task stderr = DrainAsync(process.StandardError, errors, limit, capture);
        Task<GitExit> exiting = ExitAsync(process, capture);
        bool timedOut = false;
        if (options.Timeout is { } timeout)
        {
            try { await exiting.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                timedOut = true;
                await StopAsync(process, exiting).ConfigureAwait(false);
            }
            catch (Exception) when (exiting.IsCompleted || cancellationToken.IsCancellationRequested)
            {
                // The exit fault or the caller's cancellation surfaces from the joined wait below.
            }
        }
        // Drain every channel concurrently. A fault cancels sibling waits; the caller's disposal still owns
        // stop and observed exit, even when a caller cancels midway through I/O.
        await Task.WhenAll(writing, stdout, stderr, exiting).ConfigureAwait(false);
        return new(await exiting.ConfigureAwait(false), output.ToArray(), errors.ToArray(), timedOut, limit.Exceeded);
    }

    private static async Task StopAsync(IGitProcess process, Task<GitExit> exiting)
    {
        await process.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
        try { await exiting.WaitAsync(StopGrace).ConfigureAwait(false); }
        catch (TimeoutException) { await process.ForceStopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) when (exiting.IsCompleted)
        {
            // A faulted exit wait surfaces from the joined wait in CaptureAsync.
        }
    }

    private static async Task WriteAsync(Stream stdin, ReadOnlyMemory<byte> input, CancellationTokenSource capture)
    {
        try
        {
            if (!input.IsEmpty) await stdin.WriteAsync(input, capture.Token).ConfigureAwait(false);
        }
        catch (IOException) { } // The process closed its stdin early; its exit and output still decide the result.
        catch { await capture.CancelAsync().ConfigureAwait(false); throw; }
        finally { stdin.Close(); }
    }

    private static async Task DrainAsync(Stream source, MemoryStream destination, Bound limit, CancellationTokenSource capture)
    {
        try
        {
            if (limit.Limit is null)
            {
                await source.CopyToAsync(destination, capture.Token).ConfigureAwait(false);
                return;
            }
            // Past the bound every chunk is still read, so a full pipe never blocks the child.
            byte[] chunk = new byte[ChunkSize];
            int count;
            while ((count = await source.ReadAsync(chunk, capture.Token).ConfigureAwait(false)) > 0)
                limit.Keep(destination, chunk, count);
        }
        catch { await capture.CancelAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<GitExit> ExitAsync(IGitProcess process, CancellationTokenSource capture)
    {
        try { return await process.WaitForExitAsync(capture.Token).ConfigureAwait(false); }
        catch { await capture.CancelAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>The one output bound both channels share.</summary>
    private sealed class Bound(int? limit, MemoryStream output, MemoryStream errors)
    {
        private readonly object gate = new();

        internal int? Limit { get; } = limit;

        internal bool Exceeded { get; private set; }

        internal void Keep(MemoryStream destination, byte[] chunk, int count)
        {
            lock (this.gate)
            {
                if (output.Length + errors.Length + count > this.Limit) this.Exceeded = true;
                else destination.Write(chunk, 0, count);
            }
        }
    }
}
