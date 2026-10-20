using Avalonia.Threading;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Runs one action on the UI thread for any number of requests made before it starts.</summary>
/// <remarks>
/// A view that redraws on an owner's change notification gets one notification per change, from any thread. Posting a
/// redraw for each one queues a redraw per change; a burst then freezes the window. Requests that arrive before the
/// queued run starts share it, and a request made while the run executes queues one more.
/// </remarks>
/// <gotcha>
/// Views pass <see cref="DispatcherPriority.Background"/>, so a stream of requests never starves input. A higher
/// priority lets a source that changes on every run keep the UI thread busy.
/// </gotcha>
public sealed class UiRefresh
{
    private readonly Action run;
    private readonly DispatcherPriority priority;
    private int queued;

    /// <summary>Creates a refresh that runs <paramref name="run"/> on the UI thread at <paramref name="priority"/>.</summary>
    /// <param name="run">The action to run; it runs on the UI thread.</param>
    /// <param name="priority">The dispatcher priority of the queued run.</param>
    public UiRefresh(Action run, DispatcherPriority priority)
    {
        this.run = run;
        this.priority = priority;
    }

    /// <summary>Requests a run; safe from any thread. Requests before the queued run starts share it.</summary>
    public void Request()
    {
        // Only the first request since the last queued run started posts one.
        if (Interlocked.Exchange(ref this.queued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref this.queued, 0);
            this.run();
        }, this.priority);
    }
}
