using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

internal sealed class WindowsServiceListener : IDisposable
{
    private readonly WindowsServiceOwner owner;
    private readonly byte[] capability;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim accepting = new(1);
    private readonly object gate = new();
    private NamedPipeServerStream? pending;
    private bool disposed;
    private WindowsServiceListener(WindowsServiceOwner owner, ServiceDiscovery identity, byte[] capability)
    { this.owner = owner; this.Identity = identity; this.capability = capability; }
    internal ServiceDiscovery Identity { get; }
    internal static string PipeName(WindowsPrivateDirectory directory)
    {
        string application = Path.GetFileName(Path.GetDirectoryName(directory.Path)) ?? throw new InvalidDataException("The service runtime has no application directory.");
        string sidHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsFileSecurity.User.Value)));
        return $"Darkspyre.{application}.{sidHash}";
    }
    internal static WindowsServiceListener Bind(WindowsServiceOwner owner, ServiceFamily family, Guid storeId, string buildDigest, string layoutId, Guid epoch)
    {
        var identity = new ServiceDiscovery(family, ServiceProtocol.Version, epoch, storeId, buildDigest, layoutId);
        ServiceDiscoveryMetadata.Validate(identity); owner.ClaimEndpoint();
        ServiceDiscovery? stale = ServiceDiscoveryMetadata.Read(owner.RuntimeDirectory.ReadPrivateFile(ServiceDiscoveryMetadata.FileName));
        if (stale is not null)
        {
            if (stale.Service != family) throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
            owner.RuntimeDirectory.RemoveChild(stale.CapabilityName);
            owner.RuntimeDirectory.RemoveChild(ServiceDiscoveryMetadata.FileName);
        }
        var listener = new WindowsServiceListener(owner, identity, RandomNumberGenerator.GetBytes(32));
        try
        {
            listener.pending = listener.CreatePipe(first: true);
            owner.RuntimeDirectory.PublishPrivateFile(identity.CapabilityName, listener.capability);
            owner.RuntimeDirectory.PublishPrivateFile(ServiceDiscoveryMetadata.FileName, ServiceDiscoveryMetadata.Serialize(identity));
            return listener;
        }
        catch { listener.Dispose(); throw; }
    }
    private NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        security.SetOwner(WindowsFileSecurity.User);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsFileSecurity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName(this.owner.RuntimeDirectory), PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0), 0, 0, security);
    }
    internal async Task<ServicePeer> AcceptAuthenticatedAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.stopping.Token);
        await this.accepting.WaitAsync(stop.Token).ConfigureAwait(false);
        NamedPipeServerStream? pipe = null;
        try
        {
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                pipe = this.pending!;
            }
            await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            lock (this.gate)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.pending = CreatePipe(first: false);
            }
            NamedPipeServerStream connected = pipe;
            ServicePeer peer = await ServiceHandshake.AcceptAsync(connected, this.Identity, this.capability,
                () => WindowsPipeIdentity.VerifyClient(connected), stop.Token).ConfigureAwait(false);
            pipe = null; return peer;
        }
        finally { pipe?.Dispose(); this.accepting.Release(); }
    }
    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true; this.stopping.Cancel(); this.pending?.Dispose();
        }
        CryptographicOperations.ZeroMemory(this.capability);
        if (this.owner.IsHeld)
        {
            if (ServiceDiscoveryMetadata.Read(this.owner.RuntimeDirectory.ReadPrivateFile(ServiceDiscoveryMetadata.FileName))?.Epoch == this.Identity.Epoch)
                this.owner.RuntimeDirectory.RemoveChild(ServiceDiscoveryMetadata.FileName);
            this.owner.RuntimeDirectory.RemoveChild(this.Identity.CapabilityName);
        }
    }
}
