using System.Net.Sockets;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>macOS installation, authenticated transport, ownership and detached launch.</summary>
public sealed partial class ServicePlatform : IServicePlatform
{
    /// <summary>Creates the macOS driver selected by subscriber and backend composition.</summary>
    public ServicePlatform() { }
    /// <inheritdoc />
    public IServiceInstallation OpenInstallation(ServiceRegistration registration) => new Installation(registration);
    /// <inheritdoc />
    public async Task<IServiceOwner?> TryOwnAsync(ServiceStart start, CancellationToken cancellationToken = default)
    {
        using var cache = ServicePackageCache.Open(new MacServiceFileSystem(), start.PackageCachePath);
        ServicePackagePin? pin = await cache.AdoptLaunchPinAsync(start.BuildDigest, cancellationToken).ConfigureAwait(false);
        MacServiceOwner? owner = null;
        try
        {
            owner = MacServiceOwner.TryAcquire(start.RuntimePath, start.StorePath);
            using var budget = new ServiceLaunchBudget(new MacServiceFileSystem(), start.RuntimePath);
            if (owner is null)
            {
                await budget.RecordExitAsync(start.AttemptId, normalExit: true, cancellationToken).ConfigureAwait(false);
                return null;
            }
            await budget.MarkRunningAsync(start.AttemptId, cancellationToken).ConfigureAwait(false);
            var result = new Owner(start, owner, pin);
            owner = null;
            pin = null;
            return result;
        }
        finally { owner?.Dispose(); pin?.Dispose(); }
    }

    private sealed class Installation(ServiceRegistration registration) : IServiceInstallation
    {
        public async Task<ServicePeer?> TryConnectAsync(CancellationToken cancellationToken = default)
        {
            using var runtime = MacPrivateDirectory.OpenOrCreate(registration.RuntimePath);
            try { return await MacServiceClient.ConnectAsync(runtime, registration.Family, registration.StoreId, registration.ClientBuildDigest, cancellationToken).ConfigureAwait(false); }
            catch (ServiceProtocolException error) when (error.Code == ServiceErrorCode.Unavailable) { return null; }
            catch (SocketException error) when (IsTransient(error.SocketErrorCode)) { return null; }
            catch (IOException error) when (error.InnerException is SocketException socket && IsTransient(socket.SocketErrorCode)) { return null; }
            catch (EndOfStreamException) { return null; }
        }
        public async Task<ServiceLaunchReservation> TryReserveLaunchAsync(CancellationToken cancellationToken = default)
        {
            using var budget = new ServiceLaunchBudget(new MacServiceFileSystem(), registration.RuntimePath);
            return await budget.TryReserveAsync(cancellationToken).ConfigureAwait(false);
        }
        public async Task<ServicePackagePin> PinPreferredAsync(CancellationToken cancellationToken = default)
        {
            using var cache = ServicePackageCache.Open(new MacServiceFileSystem(), registration.PackageCachePath);
            return await cache.PinPreferredAsync(cancellationToken).ConfigureAwait(false);
        }
        public ServiceLaunchObservation Launch(ServicePackagePin pin, Guid attemptId)
        {
            DetachedServiceProcess process = MacDetachedLauncher.Start(pin.CreateLaunchSpec(registration.BuildArguments(pin, attemptId), registration.UserHome, registration.ScratchDirectory), pin);
            return new(attemptId, process.ProcessId, pin.BuildDigest, process.Completion);
        }
        public async Task RecordExitAsync(Guid attemptId, bool normalExit, CancellationToken cancellationToken = default)
        {
            using var budget = new ServiceLaunchBudget(new MacServiceFileSystem(), registration.RuntimePath);
            await budget.RecordExitAsync(attemptId, normalExit, cancellationToken).ConfigureAwait(false);
        }
        public async Task ResetCrashBudgetAsync(CancellationToken cancellationToken = default)
        {
            using var budget = new ServiceLaunchBudget(new MacServiceFileSystem(), registration.RuntimePath);
            await budget.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() { }
        private static bool IsTransient(SocketError error) => error is SocketError.ConnectionRefused or SocketError.ConnectionReset
            or SocketError.ConnectionAborted or SocketError.NotConnected or SocketError.AddressNotAvailable;
    }

    private sealed class Owner : IServiceOwner
    {
        private readonly ServiceStart start;
        private readonly MacServiceOwner owner;
        private readonly ServicePackagePin pin;
        private MacServiceListener? listener;
        private bool disposed;
        internal Owner(ServiceStart start, MacServiceOwner owner, ServicePackagePin pin)
        {
            this.start = start;
            this.owner = owner;
            this.pin = pin;
            this.StagingPath = Path.Combine(start.StorePath, "staging");
            using (MacPrivateDirectory.OpenOrCreate(this.StagingPath)) { }
        }
        public Guid Epoch { get; } = Guid.NewGuid();
        public string StorePath => this.start.StorePath;
        public string StagingPath { get; }
        public Task PublishAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.listener = MacServiceListener.Bind(this.owner, this.start.Family, this.start.StoreId, this.start.BuildDigest, this.start.LayoutId, this.Epoch);
            return Task.CompletedTask;
        }
        public Task<ServicePeer> AcceptAsync(CancellationToken cancellationToken = default)
            => (this.listener ?? throw new InvalidOperationException("The service has not published readiness.")).AcceptAuthenticatedAsync(cancellationToken);
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.listener?.Dispose();
            using var budget = new ServiceLaunchBudget(new MacServiceFileSystem(), this.start.RuntimePath);
            await budget.RecordExitAsync(this.start.AttemptId, normalExit: true, cancellationToken).ConfigureAwait(false);
            await DisposeAsync().ConfigureAwait(false);
        }
        public ValueTask DisposeAsync()
        {
            if (this.disposed) return ValueTask.CompletedTask;
            this.disposed = true;
            try { this.listener?.Dispose(); }
            finally { try { this.owner.Dispose(); } finally { this.pin.Dispose(); } }
            return ValueTask.CompletedTask;
        }
    }
}
