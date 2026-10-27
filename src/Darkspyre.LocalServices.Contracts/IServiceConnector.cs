namespace Darkspyre.LocalServices;

/// <summary>Joins a registered service through subscriber-owned installation and launch operations.</summary>
public interface IServiceConnector
{
    /// <summary>Joins a compatible service or launches its verified package and returns an owned subscription.</summary>
    /// <gotcha>The caller disposes the subscription. Cancellation ends connection setup without killing the service.</gotcha>
    Task<IServiceSubscription> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Explicitly resets the registered service's shared launch budget.</summary>
    Task ResetCrashBudgetAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops a running service: joins it without launching, sends <see cref="Contracts.StopMessage"/>, and returns once no
    /// process owns it. A service that is not running, or already draining, is only waited for.</summary>
    /// <gotcha>Every connection closes, other programs' too; their calls in flight end with the closed subscription.</gotcha>
    Task StopAsync(CancellationToken cancellationToken = default);
}
