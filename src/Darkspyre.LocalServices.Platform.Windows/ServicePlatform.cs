using System.ComponentModel;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

/// <summary>Windows installation, authenticated transport, ownership and detached launch.</summary>
public sealed partial class ServicePlatform : IServicePlatform
{
    /// <summary>Creates the Windows driver selected by subscriber and backend composition.</summary>
    public ServicePlatform() { }
    /// <inheritdoc />
    public IServiceInstallation OpenInstallation(ServiceRegistration registration) => new Installation(registration);
    /// <inheritdoc />
    public async Task<IServiceOwner?> TryOwnAsync(ServiceStart start, CancellationToken cancellationToken = default)
    {
        using var cache = ServicePackageCache.Open(new WindowsServiceFileSystem(), start.PackageCachePath);
        ServicePackagePin? pin = await cache.AdoptLaunchPinAsync(start.BuildDigest, cancellationToken).ConfigureAwait(false);
        WindowsServiceOwner? owner = null;
        try
        {
            owner = WindowsServiceOwner.TryAcquire(start.RuntimePath, start.StorePath);
            using var budget = new ServiceLaunchBudget(new WindowsServiceFileSystem(), start.RuntimePath);
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
            Require();
            using var runtime = WindowsPrivateDirectory.OpenOrCreate(registration.RuntimePath);
            try { return await WindowsServiceClient.ConnectAsync(runtime, registration.Family, registration.StoreId, registration.ClientBuildDigest, cancellationToken).ConfigureAwait(false); }
            catch (ServiceProtocolException error) when (error.Code == ServiceErrorCode.Unavailable) { return null; }
            catch (TimeoutException) { return null; }
            catch (IOException error) when (IsTransient(error)) { return null; }
            catch (EndOfStreamException) { return null; }
        }
        public async Task<ServiceLaunchReservation> TryReserveLaunchAsync(CancellationToken cancellationToken = default)
        {
            Require();
            using var budget = new ServiceLaunchBudget(new WindowsServiceFileSystem(), registration.RuntimePath);
            return await budget.TryReserveAsync(cancellationToken).ConfigureAwait(false);
        }
        public async Task<ServicePackagePin> PinPreferredAsync(CancellationToken cancellationToken = default)
        {
            Require();
            using var cache = ServicePackageCache.Open(new WindowsServiceFileSystem(), registration.PackageCachePath);
            return await cache.PinPreferredAsync(cancellationToken).ConfigureAwait(false);
        }
        public ServiceLaunchObservation Launch(ServicePackagePin pin, Guid attemptId)
        {
            Require();
            DetachedServiceProcess process = WindowsDetachedLauncher.Start(pin.CreateLaunchSpec(registration.BuildArguments(pin, attemptId), registration.UserHome, registration.ScratchDirectory), pin);
            return new(attemptId, process.ProcessId, pin.BuildDigest, process.Completion);
        }
        public async Task RecordExitAsync(Guid attemptId, bool normalExit, CancellationToken cancellationToken = default)
        {
            Require();
            using var budget = new ServiceLaunchBudget(new WindowsServiceFileSystem(), registration.RuntimePath);
            await budget.RecordExitAsync(attemptId, normalExit, cancellationToken).ConfigureAwait(false);
        }
        public async Task ResetCrashBudgetAsync(CancellationToken cancellationToken = default)
        {
            Require();
            using var budget = new ServiceLaunchBudget(new WindowsServiceFileSystem(), registration.RuntimePath);
            await budget.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() { }
        private void Require() => ServiceInstaller.RequireInstalled(new WindowsServiceFileSystem(), registration);
        private static bool IsTransient(IOException error) => (error.InnerException is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff) is 2 or 109 or 231 or 232 or 233;
    }

    private sealed class Owner : IServiceOwner
    {
        private readonly ServiceStart start;
        private readonly WindowsServiceOwner owner;
        private readonly ServicePackagePin pin;
        private WindowsServiceListener? listener;
        private bool disposed;
        internal Owner(ServiceStart start, WindowsServiceOwner owner, ServicePackagePin pin)
        {
            this.start = start;
            this.owner = owner;
            this.pin = pin;
            this.StagingPath = Path.Combine(start.StorePath, "staging");
            using (WindowsPrivateDirectory.OpenOrCreate(this.StagingPath)) { }
        }
        public Guid Epoch { get; } = Guid.NewGuid();
        public string StorePath => this.start.StorePath;
        public string StagingPath { get; }
        public Task PublishAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.listener = WindowsServiceListener.Bind(this.owner, this.start.Family, this.start.StoreId, this.start.BuildDigest, this.start.LayoutId, this.Epoch);
            return Task.CompletedTask;
        }
        public Task<ServicePeer> AcceptAsync(CancellationToken cancellationToken = default)
            => (this.listener ?? throw new InvalidOperationException("The service has not published readiness.")).AcceptAuthenticatedAsync(cancellationToken);
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.listener?.Dispose();
            using var budget = new ServiceLaunchBudget(new WindowsServiceFileSystem(), this.start.RuntimePath);
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
