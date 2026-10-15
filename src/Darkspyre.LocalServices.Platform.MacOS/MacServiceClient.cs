using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.MacOS;

/// <summary>Connects to an existing authenticated owner. It performs no launch, subscription or business replay.</summary>
[SupportedOSPlatform("macos")]
public static class MacServiceClient
{
    /// <summary>Connects to the family socket and authenticates the expected store and service identity.</summary>
    public static async Task<ServicePeer> ConnectAsync(MacPrivateDirectory runtime, ServiceFamily family,
        Guid expectedStoreId, string clientBuildDigest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ServiceDiscovery discovery = ServiceDiscoveryMetadata.Read(runtime.ReadPrivateFile(ServiceDiscoveryMetadata.FileName))
            ?? throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
        if (discovery.Service != family || discovery.ProtocolVersion != ServiceProtocol.Version)
            throw new ServiceProtocolException(ServiceErrorCode.IncompatibleProtocol);
        if (discovery.StoreId != expectedStoreId) throw new ServiceProtocolException(ServiceErrorCode.StoreMismatch);
        if (clientBuildDigest is not { Length: 64 } || !clientBuildDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA256 client build identity is required.", nameof(clientBuildDigest));
        byte[] capability = runtime.ReadPrivateFile(discovery.CapabilityName)
            ?? throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
        Socket? socket = null;
        try
        {
            if (capability.Length != 32) throw new AuthenticationException("The local service capability has an invalid length.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ServiceProtocol.ConnectDeadline);
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacServiceListener.SocketPath(runtime, discovery)), deadline.Token).ConfigureAwait(false);
            var hello = new HelloMessage(family, ServiceProtocol.Version, expectedStoreId, clientBuildDigest);
            ServicePeer peer = await MacServiceHandshake.ConnectAsync(socket, discovery, capability, hello, deadline.Token).ConfigureAwait(false);
            socket = null;
            return peer;
        }
        finally
        {
            socket?.Dispose();
            CryptographicOperations.ZeroMemory(capability);
        }
    }
}
