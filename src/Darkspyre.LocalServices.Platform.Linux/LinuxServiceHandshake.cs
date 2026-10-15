using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices.Platform.Linux;

internal static class LinuxServiceHandshake
{
    internal static Task<ServicePeer> AcceptAsync(Socket socket, ServiceDiscovery identity, byte[] capability, CancellationToken token)
        => AuthenticateAsync(socket, stream => ServiceHandshake.AcceptAsync(stream, identity, capability, () => VerifyPeer(socket), token));
    internal static Task<ServicePeer> ConnectAsync(Socket socket, ServiceDiscovery identity, byte[] capability, HelloMessage hello, CancellationToken token)
        => AuthenticateAsync(socket, stream => ServiceHandshake.ConnectAsync(stream, identity, capability, hello, token));
    private static async Task<ServicePeer> AuthenticateAsync(Socket socket, Func<Stream, Task<ServicePeer>> handshake)
    {
        try { VerifyPeer(socket); }
        catch { socket.Dispose(); throw; }
        return await handshake(new NetworkStream(socket, ownsSocket: true)).ConfigureAwait(false);
    }
    private static void VerifyPeer(Socket socket)
    {
        uint length = 12;
        if (LinuxNative.GetPeerIdentity(socket.SafeHandle, 1, 17, out LinuxNative.PeerCredentials peer, ref length) != 0 || length != 12)
            throw LinuxNative.Error("SO_PEERCRED", Marshal.GetLastPInvokeError());
        if (peer.UserId != LinuxNative.EffectiveUserId()) throw new AuthenticationException("The local service peer belongs to another OS user.");
    }
}
