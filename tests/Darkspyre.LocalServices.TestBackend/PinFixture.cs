using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

internal static class PinFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string coordination = args[3];
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            if (args[0] == "pin-launcher")
            {
                using var cache = FixturePlatform.OpenCache(args[1]);
                using ServicePackagePin pin = await cache.AdoptLaunchPinAsync(args[2], deadline.Token);
                var registration = new ServiceRegistration(ServiceFamily.Memory, Path.Combine(coordination, "runtime"), args[1], Guid.NewGuid(),
                    "pin-test", args[2], args[4], args[5], (child, _) => ["pin-child", child.CachePath, child.BuildDigest, coordination]);
                using IServiceInstallation installation = FixturePlatform.Create().OpenInstallation(registration);
                _ = installation.Launch(pin, Guid.NewGuid());
                // The child already owns an inherited pin but has not adopted it in managed code.
                Environment.Exit(0);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(coordination, "child-ready.pending"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), deadline.Token);
                File.Move(Path.Combine(coordination, "child-ready.pending"), Path.Combine(coordination, "child-ready"));
                while (!File.Exists(Path.Combine(coordination, "adopt-pin"))) await Task.Delay(25, deadline.Token);
                using var cache = FixturePlatform.OpenCache(args[1]);
                using ServicePackagePin pin = await cache.AdoptLaunchPinAsync(args[2], deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(coordination, "child-adopted"), pin.BuildDigest, deadline.Token);
            }
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(coordination, "pin-failure"), error.ToString());
            return 1;
        }
    }
}
