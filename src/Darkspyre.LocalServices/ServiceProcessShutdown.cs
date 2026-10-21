using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>One named shutdown stage.</summary>
/// <param name="Name">The stable cleanup stage name.</param>
/// <param name="Run">The asynchronous cleanup operation.</param>
public sealed record ServiceCleanupStage(string Name, Func<CancellationToken, Task> Run);
/// <summary>A shutdown stage and its captured failure.</summary>
/// <param name="Stage">The failed cleanup stage name.</param>
/// <param name="Error">The reported failure, when present.</param>
public sealed record ServiceCleanupFailure(string Stage, Exception Error);
/// <summary>Completed shutdown stages and failures from attempted stages.</summary>
/// <param name="CompletedStages">The cleanup stages that completed successfully.</param>
/// <param name="Failures">The failures collected while attempting cleanup.</param>
public sealed record ServiceCleanupReport(IReadOnlyList<string> CompletedStages, IReadOnlyList<ServiceCleanupFailure> Failures)
{
    /// <summary>True when every attempted cleanup stage succeeds.</summary>
    public bool Succeeded => this.Failures.Count == 0;
}

/// <summary>
/// Backend-process-only shutdown. Independent stages are attempted after failures. A dedicated watchdog thread
/// terminates THIS process with exit code 70 if cleanup exceeds the total deadline. Never call this in a client host.
/// </summary>
public static class ServiceProcessShutdown
{
    /// <summary>The process exit code when the shutdown deadline expires.</summary>
    public const int WatchdogExitCode = 70;

    /// <summary>
    /// Call as soon as admission drains. Stages stop producers, finish/roll back work and close owned resources.
    /// The caller retains OS ownership until these stages complete. Cancellation requests cooperation after ten
    /// seconds; the watchdog terminates at fifteen seconds even if a stage blocks synchronously or ignores cancellation.
    /// </summary>
    public static async Task<ServiceCleanupReport> RunAsync(IReadOnlyList<ServiceCleanupStage> stages, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ServiceCleanupStage[] owned = [.. stages];
        if (owned.Any(stage => stage is null || string.IsNullOrWhiteSpace(stage.Name) || stage.Run is null)
            || owned.Select(stage => stage.Name).Distinct(StringComparer.Ordinal).Count() != owned.Length)
            throw new ArgumentException("Cleanup stages require distinct names and handlers.", nameof(stages));
        TimeProvider time = timeProvider ?? TimeProvider.System;
        long started = time.GetTimestamp();
        using var stopped = new ManualResetEventSlim();
        using var grace = new CancellationTokenSource(ServiceProtocol.GracefulDrainDeadline, time);
        var watchdog = new Thread(() =>
        {
            while (!stopped.Wait(TimeSpan.FromMilliseconds(25)))
                if (time.GetElapsedTime(started) >= ServiceProtocol.ShutdownDeadline)
                    Environment.Exit(WatchdogExitCode);
        })
        { IsBackground = true, Name = "local-service-shutdown-watchdog" };
        watchdog.Start();
        try
        {
            List<string> completed = [];
            List<ServiceCleanupFailure> failures = [];
            foreach (ServiceCleanupStage stage in owned)
            {
                try
                {
                    await stage.Run(grace.Token).ConfigureAwait(false);
                    completed.Add(stage.Name);
                }
                catch (Exception error) { failures.Add(new(stage.Name, error)); }
            }
            if (time.GetElapsedTime(started) >= ServiceProtocol.ShutdownDeadline) Environment.Exit(WatchdogExitCode);
            return new(completed.AsReadOnly(), failures.AsReadOnly());
        }
        finally
        {
            stopped.Set();
            watchdog.Join();
        }
    }
}
