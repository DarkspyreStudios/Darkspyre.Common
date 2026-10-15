using Darkspyre.LocalServices;

if (args.Length == 16 && args[0] == "--family")
{
    var values = Enumerable.Range(0, 8).ToDictionary(i => args[i * 2], i => args[i * 2 + 1], StringComparer.Ordinal);
    return await ServiceFixture.RunAsync(["serve", values["--package-cache-path"], values["--build-digest"],
        values["--runtime-path"], values["--store-path"], values["--store-id"], values["--layout-id"],
        values["--attempt-id"], values["--store-path"], "normal"]);
}
if (args.Length > 0 && args[0] is "pin-launcher" or "pin-child") return await PinFixture.RunAsync(args);
if (args.Length > 0 && args[0] == "launcher") return await ServiceFixture.RunLauncherAsync(args);
if (args.Length > 0 && args[0] == "serve") return await ServiceFixture.RunAsync(args);
if (args.Length is not (3 or 4)) return 2;
string coordination = args[2];
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var cache = FixturePlatform.OpenCache(args[0]);
    using ServicePackagePin pin = await cache.AdoptLaunchPinAsync(args[1], deadline.Token);
    await File.WriteAllTextAsync(Path.Combine(coordination, "ready"), pin.BuildDigest, deadline.Token);
    if (args.Length == 4 && args[3] == "watchdog")
    {
        await ServiceProcessShutdown.RunAsync([new("blocked-cleanup", _ =>
        {
            Thread.Sleep(Timeout.Infinite);
            return Task.CompletedTask;
        })], new AcceleratedClock());
        return 3;
    }
    while (!File.Exists(Path.Combine(coordination, "release")))
        await Task.Delay(25, deadline.Token);
    return 0;
}
catch (Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(coordination, "failure"), error.GetType().Name + ": " + error.Message);
    return 1;
}

// Accelerates deadlines only inside the disposable watchdog fixture, not in production configuration.
internal sealed class AcceleratedClock : TimeProvider
{
    private readonly long started = TimeProvider.System.GetTimestamp();
    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
    public override long GetTimestamp() => checked((TimeProvider.System.GetTimestamp() - this.started) * 100);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, Scale(dueTime), Scale(period));
    private static TimeSpan Scale(TimeSpan value) => value == Timeout.InfiniteTimeSpan ? value : value / 100;
}
