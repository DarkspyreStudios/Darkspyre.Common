using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Darkspyre.Agent.Common.UI.Errors;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// Subscribes <paramref name="callback"/> to one owner's change notifications until the returned subscription is
/// disposed, such as an owner's <c>SubscribeChangesAsync</c> method group.
/// </summary>
/// <param name="callback">Runs on each change, from any thread.</param>
/// <param name="cancellationToken">Cancels the subscription request.</param>
public delegate ValueTask<IAsyncDisposable> UiChangeSubscription(
    Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default);

/// <summary>A view that reads what it shows when an owner changes, and changes only the controls whose part changed.</summary>
/// <typeparam name="TShown">A record of the displayed fields only. Its collections are <see cref="ShownList{T}"/> and their items are records.</typeparam>
/// <remarks>
/// <para>
/// A view reads an owner's published view, so it has to redraw on that owner's notification rather than on its
/// parent happening to redraw.
/// </para>
/// <para>
/// <see cref="ReadAsync"/> performs the view's service reads and returns what the view shows; <see cref="Render"/>
/// changes the controls from that record only. One refresh runs at a time, on the UI thread. A change that arrives
/// during a refresh schedules exactly one more after it, so an older read never finishes last and overwrites a newer
/// one. Notifications that arrive before a queued refresh starts share that refresh, so a burst of changes costs one
/// refresh. A read equal to the last rendered record renders nothing. Watching starts when the view enters a window
/// and ends when it leaves; the first render after it enters builds the view.
/// </para>
/// </remarks>
public abstract class Watching<TShown> : UserControl where TShown : IEquatable<TShown>
{
    private readonly List<IAsyncDisposable> subscriptions = [];
    private readonly UiRefresh refresh;
    private CancellationTokenSource? lifetime;
    private bool running;
    private bool pending;
    private bool hasLast;
    private TShown? last;

    /// <summary>Creates a view whose refreshes run at background priority, so a stream of notices never starves input.</summary>
    protected Watching() =>
        this.refresh = new UiRefresh(() => _ = this.ScheduleRefreshAsync(), DispatcherPriority.Background);

    /// <summary>The owners whose change notifications refresh this view, as their subscribe methods.</summary>
    protected abstract IEnumerable<UiChangeSubscription> Owners { get; }

    /// <summary>Performs the service reads and returns what the view shows. Value-equal results mean nothing to redraw.</summary>
    protected abstract Task<TShown> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds the view the first time (<paramref name="previous"/> is default) and afterwards changes only the controls
    /// whose part of the shown record differs from <paramref name="previous"/>. Not called when the record is equal.
    /// </summary>
    /// <param name="previous">The record the last render showed, or default for the first render since the view entered a window.</param>
    /// <param name="current">The record to show.</param>
    protected abstract void Render(TShown? previous, TShown current);

    /// <summary>Runs once each time the view enters a window, before the first refresh.</summary>
    protected virtual Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Keeps <paramref name="subscription"/> until the view leaves the window, for a source that is not one of <see cref="Owners"/>.</summary>
    protected void Own(IAsyncDisposable subscription) => this.subscriptions.Add(subscription);

    /// <summary>Requests a refresh, as a change notification does.</summary>
    protected void Refresh() => this.refresh.Request();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.hasLast = false;
        this.last = default;
        CancellationTokenSource started = new();
        this.lifetime = started;
        _ = this.StartAsync(started.Token);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ = this.StopAsync();
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.InitializeAsync(cancellationToken);
            foreach (UiChangeSubscription subscribe in this.Owners)
            {
                IAsyncDisposable subscription = await subscribe(this.OnChangedAsync, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    await subscription.DisposeAsync();
                    return;
                }
                this.subscriptions.Add(subscription);
            }
            await this.ScheduleRefreshAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { this.Fail(error); }
    }

    private ValueTask OnChangedAsync(CancellationToken cancellationToken)
    {
        // Delivery never waits for the view; the refresh runs on the UI thread on its own.
        this.Refresh();
        return ValueTask.CompletedTask;
    }

    // Runs on the UI thread, so the running and pending flags and the last record need no lock.
    private async Task ScheduleRefreshAsync()
    {
        if (this.lifetime is not { } current || current.IsCancellationRequested) return;
        if (this.running)
        {
            this.pending = true;
            return;
        }
        this.running = true;
        try
        {
            do
            {
                this.pending = false;
                TShown shown = await this.ReadAsync(current.Token);
                if (current.IsCancellationRequested) return;
                if (this.hasLast && EqualityComparer<TShown>.Default.Equals(this.last, shown)) continue;
                // A render that throws leaves the last record as it was, so the next refresh renders again.
                this.Render(this.hasLast ? this.last : default, shown);
                this.last = shown;
                this.hasLast = true;
            }
            while (this.pending);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { this.Fail(error); }
        finally { this.running = false; }
    }

    // A refresh nobody awaits must still fail visibly: the nearest region shows its failure.
    private void Fail(Exception error) => UiRegion.Report(this, error);

    private async Task StopAsync()
    {
        CancellationTokenSource? ending = this.lifetime;
        this.lifetime = null;
        if (ending is null) return;
        await ending.CancelAsync();
        IAsyncDisposable[] ended = [.. this.subscriptions];
        this.subscriptions.Clear();
        foreach (IAsyncDisposable subscription in ended) await subscription.DisposeAsync();
        ending.Dispose();
    }
}
