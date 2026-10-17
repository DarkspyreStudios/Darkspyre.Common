using System.Diagnostics;
using System.Security.Authentication;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// Accepts authenticated peers for a backend and runs each on its control host. A peer that fails authentication or
/// the handshake is dropped and accepting continues.
/// </summary>
/// <gotcha>Shutdown calls <see cref="StopAcceptingAsync"/>, disposes the control host, then calls <see cref="JoinPeersAsync"/>.</gotcha>
public sealed class ServicePeerHost : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Task> peers = [];
    private readonly CancellationTokenSource accepting = new();
    private readonly IServiceOwner owner;
    private readonly ServiceControlHost control;
    private readonly Func<Guid, Task>? released;
    private readonly Task accept;

    /// <summary>Starts accepting peers on <paramref name="owner"/>.</summary>
    /// <param name="owner">The backend's published ownership.</param>
    /// <param name="control">The control host that runs each peer.</param>
    /// <param name="released">Runs with a peer's connection ID after its control loop ends, to release that connection's state.</param>
    public ServicePeerHost(IServiceOwner owner, ServiceControlHost control, Func<Guid, Task>? released = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        this.released = released;
        this.accept = AcceptAsync();
    }

    /// <summary>Stops accepting new peers and joins the accept loop.</summary>
    public async Task StopAcceptingAsync()
    {
        await this.accepting.CancelAsync().ConfigureAwait(false);
        await this.accept.ConfigureAwait(false);
    }

    /// <summary>Joins every accepted peer. Call it after the control host is disposed.</summary>
    public Task JoinPeersAsync()
    {
        Task[] active;
        lock (this.gate) active = [.. this.peers];
        return Task.WhenAll(active);
    }

    /// <summary>Releases the accept loop's cancellation source. Call it after <see cref="StopAcceptingAsync"/>.</summary>
    public void Dispose() => this.accepting.Dispose();

    private async Task AcceptAsync()
    {
        while (!this.accepting.IsCancellationRequested)
        {
            try
            {
                ServicePeer peer = await this.owner.AcceptAsync(this.accepting.Token).ConfigureAwait(false);
                Task serving = ServeAsync(peer);
                lock (this.gate) this.peers.Add(serving);
                _ = ObserveAsync(serving);
            }
            catch (OperationCanceledException) when (this.accepting.IsCancellationRequested) { return; }
            catch (AuthenticationException) { }
            catch (ServiceProtocolException) { }
        }
    }

    private async Task ServeAsync(ServicePeer peer)
    {
        try { await this.control.RunPeerAsync(peer).ConfigureAwait(false); }
        finally
        {
            if (this.released is { } release) await release(peer.Welcome.ConnectionId).ConfigureAwait(false);
        }
    }

    private async Task ObserveAsync(Task serving)
    {
        try { await serving.ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("A local service subscriber's cleanup failed. {0}", error); }
        finally { lock (this.gate) this.peers.Remove(serving); }
    }
}
