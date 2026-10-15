using System.Security.Authentication;
using System.Security.Cryptography;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

internal static class ServiceHandshake
{
    internal static async Task<ServicePeer> AcceptAsync(Stream stream, ServiceDiscovery identity, byte[] capability, Action verifyPeer, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ServiceProtocol.FirstSubscriberDeadline);
            byte[] supplied = new byte[48]; // Epoch (16 bytes, Guid wire order) and capability (32 bytes).
            try
            {
                await stream.ReadExactlyAsync(supplied, deadline.Token).ConfigureAwait(false);
                verifyPeer();
                if (new Guid(supplied.AsSpan(0, 16)) != identity.Epoch
                    || !CryptographicOperations.FixedTimeEquals(supplied.AsSpan(16), capability))
                    throw new AuthenticationException("The local service capability or epoch is invalid.");
            }
            finally { CryptographicOperations.ZeroMemory(supplied); }
            if (await ServiceFrameCodec.ReadAsync(stream, deadline.Token).ConfigureAwait(false) is not HelloMessage hello)
                throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
            ServiceErrorCode? error = hello.Service != identity.Service || hello.ProtocolVersion != identity.ProtocolVersion
                ? ServiceErrorCode.IncompatibleProtocol
                : hello.ExpectedStoreId != identity.StoreId ? ServiceErrorCode.StoreMismatch : null;
            if (error is not null)
            {
                await ServiceFrameCodec.WriteAsync(stream, new ProtocolErrorMessage(new(error.Value, Guid.NewGuid())), deadline.Token).ConfigureAwait(false);
                throw new ServiceProtocolException(error.Value);
            }
            if (hello.ClientBuildDigest is not { Length: 64 } || !hello.ClientBuildDigest.All(Uri.IsHexDigit))
                throw new ServiceProtocolException(ServiceErrorCode.InvalidFrame);
            var welcome = new WelcomeMessage(identity.Service, identity.ProtocolVersion, identity.StoreId, identity.Epoch,
                Guid.NewGuid(), identity.BuildDigest, identity.LayoutId);
            await ServiceFrameCodec.WriteAsync(stream, welcome, deadline.Token).ConfigureAwait(false);
            return new ServicePeer(stream, hello, welcome);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task<ServicePeer> ConnectAsync(Stream stream, ServiceDiscovery identity, byte[] capability,
        HelloMessage hello, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ServiceProtocol.FirstSubscriberDeadline);
            byte[] supplied = new byte[48];
            try
            {
                if (!identity.Epoch.TryWriteBytes(supplied)) throw new InvalidOperationException("The authentication prelude cannot hold its epoch.");
                capability.CopyTo(supplied, 16);
                await stream.WriteAsync(supplied, deadline.Token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(supplied); }
            await ServiceFrameCodec.WriteAsync(stream, hello, deadline.Token).ConfigureAwait(false);
            ServiceMessage? response = await ServiceFrameCodec.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
            if (response is null) throw new ServiceProtocolException(ServiceErrorCode.Unavailable);
            if (response is ProtocolErrorMessage rejection) throw new ServiceProtocolException(rejection.Error.Code);
            if (response is not WelcomeMessage welcome || welcome.Epoch != identity.Epoch || welcome.Service != identity.Service
                || welcome.ProtocolVersion != identity.ProtocolVersion || welcome.StoreId != identity.StoreId
                || welcome.ConnectionId == Guid.Empty || welcome.BuildDigest != identity.BuildDigest || welcome.LayoutId != identity.LayoutId)
                throw new ServiceProtocolException(ServiceErrorCode.InvalidState);
            return new ServicePeer(stream, hello, welcome);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

}
