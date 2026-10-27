using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>A family-owned socket listener. The host drains/disposes transferred peers before releasing its owner.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacServiceListener : IDisposable
{
    private readonly MacServiceOwner owner;
    private readonly Socket listener;
    private readonly byte[] capability;
    private readonly SemaphoreSlim handshakes = new(ServiceProtocol.MaximumConnectionRequests);
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    private MacServiceListener(MacServiceOwner owner, Socket listener, ServiceDiscovery identity, byte[] capability)
    {
        this.owner = owner;
        this.listener = listener;
        this.Identity = identity;
        this.capability = capability;
    }

    /// <summary>The service identity published by this listener.</summary>
    public ServiceDiscovery Identity { get; }

    /// <summary>Binds the family socket while the caller holds service ownership.</summary>
    public static MacServiceListener Bind(MacServiceOwner owner, ServiceFamily family, Guid storeId, string buildDigest, string layoutId, Guid epoch)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsHeld) throw new InvalidOperationException("Service ownership is required before binding.");
        var identity = new ServiceDiscovery(family, ServiceProtocol.Version, epoch, storeId, buildDigest, layoutId);
        ServiceDiscoveryMetadata.Validate(identity);
        string name = SocketName(identity);
        owner.ClaimEndpoint();
        ServiceDiscovery? stale = ServiceDiscoveryMetadata.Read(owner.RuntimeDirectory.ReadPrivateFile(ServiceDiscoveryMetadata.FileName));
        if (stale is not null)
        {
            if (stale.Service != family) throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
            owner.RuntimeDirectory.RemoveChild($"{stale.Epoch:N}.sock");
            owner.RuntimeDirectory.RemoveChild(stale.CapabilityName);
            owner.RuntimeDirectory.RemoveChild(ServiceDiscoveryMetadata.FileName);
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        byte[] capability = RandomNumberGenerator.GetBytes(32);
        var result = new MacServiceListener(owner, socket, identity, capability);
        try
        {
            // Bound by its short name from the runtime directory; chmod takes the full path, which has no such limit.
            MacThreadDirectory.Run(owner.RuntimeDirectory, () => { socket.Bind(new UnixDomainSocketEndPoint(name)); return true; });
            File.SetUnixFileMode(Path.Combine(owner.RuntimeDirectory.Path, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(ServiceProtocol.MaximumConnectionRequests);
            owner.RuntimeDirectory.PublishPrivateFile(identity.CapabilityName, capability);
            owner.RuntimeDirectory.PublishPrivateFile(ServiceDiscoveryMetadata.FileName, ServiceDiscoveryMetadata.Serialize(identity));
            return result;
        }
        catch (Exception failure)
        {
            try { result.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    /// <summary>Performs only authentication/negotiation; the caller must admit the peer's subscription separately.</summary>
    public async Task<ServicePeer> AcceptAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        if (!await this.handshakes.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ServiceProtocolException(ServiceErrorCode.Busy);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.stopping.Token);
            Socket socket = await this.listener.AcceptAsync(stop.Token).ConfigureAwait(false);
            return await MacServiceHandshake.AcceptAsync(socket, this.Identity, this.capability, stop.Token).ConfigureAwait(false);
        }
        finally { this.handshakes.Release(); }
    }

    // The socket's file name in the runtime directory. Bind and connect name it relative to that directory.
    internal static string SocketName(ServiceDiscovery identity) => $"{identity.Epoch:N}.sock";

    /// <summary>Stops listener admission and releases its owned socket resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
        List<Exception> failures = [];
        Attempt(() => this.stopping.Cancel());
        Attempt(this.listener.Dispose);
        CryptographicOperations.ZeroMemory(this.capability);
        if (this.owner.IsHeld)
        {
            Attempt(() =>
            {
                if (ServiceDiscoveryMetadata.Read(this.owner.RuntimeDirectory.ReadPrivateFile(ServiceDiscoveryMetadata.FileName))?.Epoch == this.Identity.Epoch)
                    this.owner.RuntimeDirectory.RemoveChild(ServiceDiscoveryMetadata.FileName);
            });
            Attempt(() => this.owner.RuntimeDirectory.RemoveChild($"{this.Identity.Epoch:N}.sock"));
            Attempt(() => this.owner.RuntimeDirectory.RemoveChild(this.Identity.CapabilityName));
        }
        // Pending accept continuations still use the semaphore/token source. They are managed-only and remain valid.
        if (failures.Count != 0) throw new AggregateException("Local service endpoint cleanup failed.", failures);
        void Attempt(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
    }
}
