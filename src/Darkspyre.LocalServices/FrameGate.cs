using System.Diagnostics;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.LocalServices;

/// <summary>
/// One connection's outbound frame order, with a queue for the frames that start and stop a call and a queue for the
/// frames that carry data. Every outbound frame takes it before it is written and releases it when the write ends, so
/// a connection still writes one frame at a time. When the gate frees, a waiting frame that is not a body chunk is
/// written before a waiting body chunk, whatever order they arrived in.
/// </summary>
/// <remarks>
/// A frame write waits as long as the peer takes to read it, and a body chunk carries up to
/// <see cref="ServiceProtocol.MaximumChunkBytes"/>, so before this a Cancel meant to stop a call, and the request of a
/// small call, waited behind other callers' data for longer than the whole deadline: twelve calls of one connection
/// ended between 64,556 ms and 95,800 ms against a 30,000 ms deadline (DSA-1291 comment 17561).
/// A body chunk is not starved and needs no rule against it: a connection admits at most
/// <see cref="ServiceProtocol.MaximumConnectionRequests"/> requests, each of which writes at most one request frame and
/// one Cancel in one direction and one Accepted and one Result in the other, so a chunk waits for at most twice that
/// many frames, each of at most one frame's bytes.
/// </remarks>
internal sealed class FrameGate : IDisposable
{
    private readonly object gate = new();
    private readonly LinkedList<Waiter> control = [], data = [];
    private bool held;
    private bool disposed;

    /// <summary>
    /// Takes the gate for <paramref name="message"/> and returns the hold, which releases it when it is disposed. The
    /// message decides its queue: a <see cref="BlobChunkMessage"/> carries data and every other message starts, stops
    /// or answers a call.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the gate was taken.</exception>
    /// <exception cref="ObjectDisposedException">The gate was disposed before it was taken.</exception>
    /// <gotcha>Each call site passes the same token it took its write semaphore under before this, so a frame still
    /// stops waiting when its connection stops: the subscription passes the token its <c>SendAsync</c> was given, and
    /// the control host passes its caller's token linked with its connection's stop. A gate taken under the caller's
    /// token alone would leave a frame waiting through a connection teardown.</gotcha>
    internal async ValueTask<Hold> EnterAsync(ServiceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.held)
            {
                this.held = true;
                return new Hold(this, TimeSpan.Zero);
            }
            // Joined before this method returns, so the order two callers reach the gate in is theirs and not the
            // scheduler's, and a caller that sees an unfinished wait knows it is already in the queue.
            waiter = new Waiter();
            (message is BlobChunkMessage ? this.data : this.control).AddLast(waiter);
        }
        long waiting = Stopwatch.GetTimestamp();
        using (cancellationToken.Register(() => waiter.Entered.TrySetCanceled(cancellationToken)))
            await waiter.Entered.Task.ConfigureAwait(false);
        return new Hold(this, Stopwatch.GetElapsedTime(waiting));
    }

    /// <summary>Releases every waiter with <see cref="ObjectDisposedException"/> and the gate's own resources.</summary>
    public void Dispose()
    {
        Waiter[] waiting;
        lock (this.gate)
        {
            if (this.disposed) return;
            this.disposed = true;
            waiting = [.. this.control, .. this.data];
            this.control.Clear();
            this.data.Clear();
        }
        foreach (Waiter waiter in waiting) waiter.Entered.TrySetException(new ObjectDisposedException(nameof(FrameGate)));
    }

    private void Release()
    {
        lock (this.gate)
        {
            // A waiter whose caller gave up cannot be handed the gate, so the next one takes it.
            while (Next() is { } waiter)
                if (waiter.Entered.TrySetResult()) return;
            this.held = false;
        }
    }

    // The next frame to write: one that starts or stops a call before one that carries data.
    private Waiter? Next()
    {
        LinkedList<Waiter> queue = this.control.Count > 0 ? this.control : this.data;
        if (queue.First is not { } first) return null;
        queue.RemoveFirst();
        return first.Value;
    }

    /// <summary>The gate, held until this is disposed. Disposing it twice releases it once.</summary>
    internal sealed class Hold(FrameGate gate, TimeSpan waited) : IDisposable
    {
        private FrameGate? holding = gate;

        /// <summary>How long this frame waited for its turn, which is <see cref="TimeSpan.Zero"/> when it did not wait.</summary>
        internal TimeSpan Waited => waited;

        /// <summary>Releases the gate to the next waiting frame.</summary>
        public void Dispose() => Interlocked.Exchange(ref this.holding, null)?.Release();
    }

    private sealed class Waiter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
