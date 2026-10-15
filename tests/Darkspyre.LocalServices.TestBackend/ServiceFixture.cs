using System.Security.Authentication;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

internal static class ServiceFixture
{
    // launcher, cache, digest, runtime, store, store-id, layout, coordination, home, scratch
    public static async Task<int> RunLauncherAsync(string[] args)
    {
        if (args.Length != 10) return 2;
        string coordination = args[7];
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var cache = FixturePlatform.OpenCache(args[1]);
            using ServicePackagePin parentPin = await cache.AdoptLaunchPinAsync(args[2], deadline.Token);
            var registration = new ServiceRegistration(ServiceFamily.Memory, args[3], args[1], Guid.Parse(args[5]),
                args[6], args[2], args[8], args[9],
                (pin, attempt) => ["serve", pin.CachePath, pin.BuildDigest, args[3], args[4], args[5], args[6], attempt.ToString("D"), coordination, "normal"]);
            List<Task<ServiceProcessExit>> exits = [];
            var connector = new ServiceConnector(FixturePlatform.Create(), registration, launch => exits.Add(launch.Completion));
            await using IServiceSubscription subscriber = await connector.ConnectAsync(deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(coordination, "launcher-ready"), subscriber.Identity.Epoch.ToString("D"), deadline.Token);
            while (!File.Exists(Path.Combine(coordination, "exit-launcher")))
            {
                foreach (Task<ServiceProcessExit> exit in exits.Where(exit => exit.IsCompleted)) _ = await exit;
                await Task.Delay(25, deadline.Token);
            }
            // Deliberately bypass managed disposal. Another application's subscriber must retain the backend.
            Environment.Exit(0);
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(coordination, "launcher-failure"), error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    // serve, cache, digest, runtime, store, store-id, layout, attempt, coordination, mode
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 10) return 2;
        using var startup = new CancellationTokenSource(ServiceProtocol.FirstSubscriberDeadline);
        string coordination = args[8];
        Guid attempt = Guid.Parse(args[7]);
        try
        {
            if (args[9] == "crash") return 17;
            await using IServiceOwner? owner = await FixturePlatform.Create().TryOwnAsync(new(ServiceFamily.Memory,
                Guid.Parse(args[5]), args[6], args[3], args[4], args[1], args[2], attempt), startup.Token);
            if (owner is null) return 0;
            await owner.PublishAsync(startup.Token);
            await using var control = new ServiceControlHost(owner.Epoch, new HashSet<string> { "echo" },
                async (context, token) =>
                {
                    var result = new MemoryStream();
                    await context.Body.CopyToAsync(result, token);
                    result.Position = 0;
                    return new(ServiceResultStatus.Succeeded, result);
                }, owner.StagingPath, startupTimestamp: TimeProvider.System.GetTimestamp());
            using var accepting = new CancellationTokenSource();
            List<Task> peers = [];
            Task accept = AcceptAsync();
            await control.Draining;
            ServiceCleanupReport cleanup = await ServiceProcessShutdown.RunAsync([
                new("accept", async _ => { accepting.Cancel(); await accept; }),
                new("subscribers", async _ => { await control.DisposeAsync(); await Task.WhenAll(peers); }),
            ]);
            if (!cleanup.Succeeded) Environment.Exit(18); // Retain ownership until abnormal process exit.
            await owner.CompleteAsync();
            await File.WriteAllTextAsync(Path.Combine(coordination, $"finished-{owner.Epoch:N}"), "clean");
            return 0;

            async Task AcceptAsync()
            {
                while (!accepting.IsCancellationRequested)
                {
                    try
                    {
                        ServicePeer peer = await owner.AcceptAsync(accepting.Token);
                        peers.Add(control.RunPeerAsync(peer));
                    }
                    catch (OperationCanceledException) when (accepting.IsCancellationRequested) { return; }
                    catch (AuthenticationException) { }
                    catch (ServiceProtocolException) { }
                }
            }
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(coordination, $"failure-{attempt:N}"), error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }
}
