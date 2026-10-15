using System.IO.Pipes;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Windows;

internal static class WindowsServiceClient
{
    internal static async Task<ServicePeer> ConnectAsync(WindowsPrivateDirectory runtime, ServiceFamily family,
        Guid expectedStoreId, string clientBuildDigest, CancellationToken cancellationToken)
    {
        ServiceDiscovery discovery = ServiceDiscoveryMetadata.Read(runtime.ReadPrivateFile(ServiceDiscoveryMetadata.FileName))
            ?? throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
        if (discovery.Service != family || discovery.ProtocolVersion != ServiceProtocol.Version)
            throw new ServiceProtocolException(ServiceErrorCode.IncompatibleProtocol);
        if (discovery.StoreId != expectedStoreId) throw new ServiceProtocolException(ServiceErrorCode.StoreMismatch);
        byte[] capability = runtime.ReadPrivateFile(discovery.CapabilityName) ?? throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
        NamedPipeClientStream? pipe = null;
        try
        {
            if (capability.Length != 32) throw new AuthenticationException("The local service capability has an invalid length.");
            pipe = new NamedPipeClientStream(".", WindowsServiceListener.PipeName(runtime), PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            // Discovery may outlive a crashed owner. Probe once; the shared connector owns launch/retry deadlines.
            await pipe.ConnectAsync(0, cancellationToken).ConfigureAwait(false);
            WindowsPipeIdentity.VerifyServer(pipe);
            ServicePeer peer = await ServiceHandshake.ConnectAsync(pipe, discovery, capability,
                new HelloMessage(family, ServiceProtocol.Version, expectedStoreId, clientBuildDigest), cancellationToken).ConfigureAwait(false);
            pipe = null; return peer;
        }
        finally { pipe?.Dispose(); CryptographicOperations.ZeroMemory(capability); }
    }
}
