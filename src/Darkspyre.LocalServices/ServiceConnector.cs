using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Connects/subscribes to a compatible owner or launches the preferred verified package. It retries only before
/// returning a subscription. It never retries business work, replaces a live owner or edits storage sidecars.
/// </summary>
public sealed class ServiceConnector : IServiceConnector
{
    private readonly IServicePlatform platform;
    private readonly ServiceRegistration registration;
    private readonly Action<ServiceLaunchObservation> observeLaunch;

    /// <summary>Creates the library-owned connector for a trusted installed service.</summary>
    /// <param name="platform">The build-selected native service operations.</param>
    /// <param name="registration">Trusted installed service identity, paths and argument construction.</param>
    /// <param name="observeLaunch">A trusted, prompt observer that must consume Completion, including accounting failures.</param>
    public ServiceConnector(IServicePlatform platform, ServiceRegistration registration, Action<ServiceLaunchObservation> observeLaunch)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(observeLaunch);
        if (!Enum.IsDefined(registration.Family) || registration.StoreId == Guid.Empty
            || string.IsNullOrWhiteSpace(registration.LayoutId) || registration.LayoutId.Length > 128 || registration.LayoutId.Any(char.IsControl)
            || registration.ClientBuildDigest is not { Length: 64 } || !registration.ClientBuildDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("A registered service requires family, store, layout and client-build identities.", nameof(registration));
        ArgumentNullException.ThrowIfNull(registration.BuildArguments);
        this.platform = platform;
        this.registration = registration;
        this.observeLaunch = observeLaunch;
    }

    /// <summary>Joins a compatible owner or starts the preferred verified package.</summary>
    public async Task<IServiceSubscription> ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ServiceProtocol.ConnectDeadline);
        using IServiceInstallation installation = this.platform.OpenInstallation(this.registration);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ServiceSubscription? connected = await TryConnectAsync(installation, deadline.Token).ConfigureAwait(false);
                if (connected is not null) return connected;
                ServiceLaunchReservation reservation = await installation.TryReserveLaunchAsync(deadline.Token).ConfigureAwait(false);
                if (reservation.Decision == ServiceLaunchDecision.Exhausted)
                    throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
                if (reservation.Decision == ServiceLaunchDecision.Reserved)
                {
                    Guid attempt = reservation.AttemptId!.Value;
                    try
                    {
                        using ServicePackagePin pin = await installation.PinPreferredAsync(deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        ServiceLaunchObservation launch = installation.Launch(pin, attempt);
                        this.observeLaunch(launch with { Completion = ObserveExitAsync(launch) });
                    }
                    catch
                    {
                        await installation.RecordExitAsync(attempt, normalExit: false, CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
        }
    }

    /// <summary>Explicit retry from the subscriber library, not an automatic response to failed startup.</summary>
    public async Task ResetCrashBudgetAsync(CancellationToken cancellationToken = default)
    {
        using IServiceInstallation installation = this.platform.OpenInstallation(this.registration);
        await installation.ResetCrashBudgetAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ServiceSubscription?> TryConnectAsync(IServiceInstallation installation, CancellationToken cancellationToken)
    {
        ServicePeer? peer = null;
        try
        {
            peer = await installation.TryConnectAsync(cancellationToken).ConfigureAwait(false);
            if (peer is null) return null;
            if (peer.Welcome.LayoutId != this.registration.LayoutId)
                throw new ServiceProtocolException(ServiceErrorCode.IncompatibleProtocol);
            ServicePeer subscribing = peer;
            peer = null; // SubscribeAsync owns disposal even when admission fails.
            return await ServiceSubscription.SubscribeAsync(subscribing, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceProtocolException error) when (error.Code is ServiceErrorCode.Unavailable or ServiceErrorCode.ServiceDraining) { return null; }
        catch (EndOfStreamException) { return null; }
        finally { if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task<ServiceProcessExit> ObserveExitAsync(ServiceLaunchObservation launch)
    {
        ServiceProcessExit exit = await launch.Completion.ConfigureAwait(false);
        using IServiceInstallation installation = this.platform.OpenInstallation(this.registration);
        await installation.RecordExitAsync(launch.AttemptId, normalExit: exit.ExitCode == 0).ConfigureAwait(false);
        return exit;
    }
}
