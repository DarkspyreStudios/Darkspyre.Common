using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Errors;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>A transcript's top row and its scroll offset in pixels.</summary>
/// <param name="Row">The top row's stable key.</param>
/// <param name="Offset">Pixels scrolled past its top.</param>
public sealed record TranscriptAnchor(string Row, double Offset);

/// <summary>
/// Moves a transcript's scrolling pane. Every row of the shown window is drawn at its real height, so the mouse wheel
/// moves the content by a fixed number of pixels. The pane opens at its recorded anchor, a row name and the pixels of
/// that row above the top edge, or at the latest message when there is none. While it is locked to the latest
/// message, new content keeps the view at the bottom; a <see cref="FollowLatest"/> keeps the pane there and detects the
/// person's scrolling. Scrolled back, the pane never moves on its own: each row
/// registers with the pane's scroll anchoring, so rows that load, release or change size above the row at the top of
/// the view leave that row where it is on screen. Only the person's scrolling locks or unlocks the pane: scrolling to
/// the bottom locks it and tells its owner, which releases the older messages; scrolling up unlocks it. After each
/// layout pass it asks its owner for older messages when the rows do not fill the pane, or when the person scrolled back
/// to within <see cref="PrefetchMessages"/> of the oldest loaded message; a load that added nothing is not asked again
/// for the same window. A push at the top, a wheel turn, key or scroll gesture upwards with the pane already at the top,
/// asks even then, and stays active until the content grows or no older messages are left. The pane reports where the
/// person scrolls once scrolling rests, and shows the Scroll to newest button while the pane is not at the bottom.
/// Older messages are never released while a left press is held in the pane, or while the transcript holds a selection
/// the person has not moved on from (a press outside the transcript, or keyboard focus entering a text box outside it):
/// a release due meanwhile runs once that ends, so a drag and its Copy keep every row the selection covers.
/// </summary>
/// <gotcha>
/// Every correction runs in <see cref="ScrollViewer.ScrollChanged"/>, which the pane raises at the end of a layout
/// pass; an offset set there is laid out again before the frame is drawn. A correction posted to the dispatcher at
/// Loaded priority draws one frame at the wrong place first.
/// </gotcha>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The running load's own finally disposes its cancellation source; the field only names it so Detach can cancel it.")]
public sealed class TranscriptScroll
{
    // How long scrolling must rest before the position is reported.
    private static readonly TimeSpan ReportDelay = TimeSpan.FromMilliseconds(200);
    // How close, in messages, the first message in view may come to the oldest loaded one before the previous messages
    // load. Thirty messages are at least a screen of short turns, so the load finishes before the person reaches the
    // top, where the pane's scroll anchoring is off.
    internal const int PrefetchMessages = 30;
    // Content counts as grown only past a pixel: layout leaves fractions of one.
    private const double GrowthTolerance = 1;
    // Avalonia's scroll anchoring holds the row in view only while the offset is at least this far from the top.
    private const double AnchoringStarts = 0.1;
    private readonly ScrollViewer pane;
    private readonly FollowLatest follow;
    private readonly Control latest;
    private readonly TextSelectionScope selection;
    // The window whose presses and focus tell whether the person moved on from the selection, while the scope is in it.
    private TopLevel? watched;
    // The person pressed or tabbed outside the transcript since its selection last changed.
    private bool movedOn;
    // A release of older messages came while one was deferred.
    private bool due;
    private readonly DispatcherTimer reportTimer = new() { Interval = ReportDelay };
    private TranscriptList? list;
    private Owner? owner;
    // The anchor the pane opens at, until its row is laid out.
    private TranscriptAnchor? start;
    // The window the owner shows: the first message's position and how many messages it holds.
    private (int Offset, int Count) window;
    // The window the last load started from and whether it added messages. A load that added nothing is not asked
    // again for the same window, and one that added messages waits for the owner to show the new window. The owner
    // showing another window forgets it, so a window shown again later, as after a release, is asked like a new one.
    private ((int Offset, int Count) Window, bool Progressed)? settled;
    // The content height when the person pushed at the top, while that push still asks for older messages.
    private double? pushed;
    // The load running now; one at a time.
    private CancellationTokenSource? loading;
    // At the very top, where anchoring is off: the first row in view and where it sat in the content.
    private (Control Row, double Y)? top;

    /// <summary>
    /// Drives <paramref name="pane"/>; <paramref name="latest"/> is the Scroll to newest button and
    /// <paramref name="selection"/> the transcript's selection scope, which wraps the pane.
    /// </summary>
    public TranscriptScroll(ScrollViewer pane, Control latest, TextSelectionScope selection)
    {
        this.pane = pane;
        this.selection = selection;
        this.follow = new FollowLatest(pane);
        this.follow.Scrolled += (_, _) => this.OnScrolled();
        this.follow.Locked += (_, _) => this.Release();
        this.latest = latest;
        // A press held in the pane, as a drag that selects text is; it ends with the release or a cancelled capture.
        pane.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (args.GetCurrentPoint(pane).Properties.IsLeftButtonPressed) this.Held = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.PointerReleasedEvent, (_, _) => this.EndHold(), RoutingStrategies.Bubble, handledEventsToo: true);
        pane.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => this.EndHold(), RoutingStrategies.Direct, handledEventsToo: true);
        selection.SelectionChanged += (_, _) =>
        {
            this.movedOn = false;
            this.RunDue();
        };
        selection.AttachedToVisualTree += (_, _) => this.Watch(TopLevel.GetTopLevel(selection));
        selection.DetachedFromVisualTree += (_, _) => this.Watch(null);
        this.latest.IsVisible = false;
        this.reportTimer.Tick += (_, _) =>
        {
            this.reportTimer.Stop();
            this.Report();
        };
        pane.ScrollChanged += this.OnScrollChanged;
        // The one path that asks for older messages after the rows change: the end of every layout pass.
        pane.LayoutUpdated += (_, _) => this.Evaluate();
        // A push at the top: the pane cannot scroll further up, so only older messages can answer it.
        pane.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
        {
            if (args.Delta.Y > 0) this.Push();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            // Keys typed into a text box, such as a queued prompt's edit box, move its caret, not the pane.
            if (args.Key is Key.Up or Key.PageUp or Key.Home && args.Source is not TextBox) this.Push();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.ScrollGestureEvent, (_, args) =>
        {
            if (args.Delta.Y < 0) this.Push();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Opens the conversation <paramref name="key"/>, whose rows <paramref name="rows"/> shows, at <paramref name="start"/>, or at the latest message when it is
    /// null or names a row the list does not show. <paramref name="scrolled"/> hears where the person scrolls: the row at the top and its offset, or null
    /// while the pane follows the latest message. <paramref name="loadOlder"/> loads older messages and returns true only
    /// when it added messages. <paramref name="release"/> hears that the person returned to the latest message.
    /// </summary>
    public void Attach(TranscriptList rows, string key, TranscriptAnchor? start, Action<TranscriptAnchor?> scrolled,
        Func<CancellationToken, Task<bool>> loadOlder, Action release)
    {
        if (this.owner?.Key == key && ReferenceEquals(this.list, rows)) return;
        this.list = rows;
        this.owner = new Owner(key, scrolled, loadOlder, release);
        this.follow.Open(start is null || rows.Rows.All(row => row.Key != start.Row));
        this.start = this.follow.Following ? null : start;
        this.top = null;
        this.ForgetLoads();
        this.reportTimer.Stop();
        this.Place();
    }

    /// <summary>Stops driving the pane, as when no conversation is shown, and cancels a load that is still running.</summary>
    public void Detach()
    {
        this.owner = null;
        this.list = null;
        this.start = null;
        this.top = null;
        this.ForgetLoads();
        this.reportTimer.Stop();
        this.latest.IsVisible = false;
    }

    /// <summary>Whether the rows from <paramref name="position"/> on are at least a panel tall.</summary>
    public bool FillsFrom(int position)
    {
        if (this.list is not { } rows || this.pane.Viewport.Height <= 0) return false;
        double height = 0;
        foreach (Control container in rows.Items.GetRealizedContainers())
            if (container.DataContext is TranscriptRow { } row && row.Position >= position) height += container.Bounds.Height;
        return height >= this.pane.Viewport.Height;
    }

    /// <summary>
    /// The owner has shown a new window, starting at <paramref name="messageOffset"/> and holding
    /// <paramref name="messageCount"/> messages; the pane asks for more once it has laid it out.
    /// </summary>
    public void Shown(int messageOffset, int messageCount)
    {
        // A release returns the window to one a load already started from; a load recorded before this window is stale.
        if (this.window != (messageOffset, messageCount)) this.settled = null;
        this.window = (messageOffset, messageCount);
        // The layout pass that follows evaluates the new window, also when its rows kept their sizes.
        this.pane.InvalidateMeasure();
    }

    /// <summary>Whether the pane is locked to the latest message.</summary>
    public bool Following => this.follow.Following;

    /// <summary>True from a left-button press inside the pane until it is released or its capture is lost.</summary>
    internal bool Held { get; private set; }

    // A press is held, or the transcript holds a selection the person has not moved on from.
    private bool Deferred => this.Held || (this.selection.CanCopy && !this.movedOn);

    /// <summary>
    /// The one way the transcript releases its older messages: at once, or, while a press is held or a selection stands
    /// that the person has not moved on from, once that ends.
    /// </summary>
    public void Release()
    {
        if (this.Deferred) this.due = true;
        else this.owner?.Release();
    }

    private void EndHold()
    {
        this.Held = false;
        this.RunDue();
    }

    // A release that came while one was deferred runs once nothing defers it, if the pane still follows.
    private void RunDue()
    {
        if (!this.due || this.Deferred) return;
        this.due = false;
        if (this.follow.Following) this.owner?.Release();
    }

    // The window-level handlers live while the scope is in a window, so a dropped pane is not held by the window.
    private void Watch(TopLevel? next)
    {
        if (this.watched is { } previous)
        {
            previous.RemoveHandler(InputElement.PointerPressedEvent, this.WindowPressed);
            previous.RemoveHandler(InputElement.GotFocusEvent, this.WindowFocused);
        }
        this.watched = next;
        if (next is null) return;
        next.AddHandler(InputElement.PointerPressedEvent, this.WindowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        next.AddHandler(InputElement.GotFocusEvent, this.WindowFocused, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // A press outside the transcript moves on from its selection.
    private void WindowPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Visual source && this.selection.IsVisualAncestorOf(source)) return;
        this.MoveOn();
    }

    // Keyboard focus entering a text box outside the transcript, such as Tab into the composer, moves on too.
    private void WindowFocused(object? sender, FocusChangedEventArgs args)
    {
        if (args.Source is TextBox box && !this.selection.IsVisualAncestorOf(box)) this.MoveOn();
    }

    private void MoveOn()
    {
        this.movedOn = true;
        this.RunDue();
    }

    /// <summary>Follows the latest message again and reports it.</summary>
    public void Latest()
    {
        this.start = null;
        this.follow.Latest();
        this.Place();
        this.Report();
    }

    // The person moved the pane: the opening anchor and the very-top row no longer hold, and the position is reported
    // once scrolling rests.
    private void OnScrolled()
    {
        if (this.owner is null) return;
        this.start = null;
        this.top = null;
        this.reportTimer.Stop();
        this.reportTimer.Start();
    }

    // Raised at the end of every layout pass that changed the extent, the pane or the offset.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (this.owner is null) return;
        this.Place();
    }

    // Puts the pane where it belongs after layout: at the opening anchor until its row is laid out, and at the very top,
    // where anchoring is off, below what arrived above the first row. FollowLatest keeps it at the bottom while locked.
    private void Place()
    {
        if (this.list is null) return;
        if (this.start is { } opening)
        {
            if (this.Container(opening.Row) is { } row && row.TranslatePoint(default, this.pane) is { } at)
            {
                this.follow.Move(this.pane.Offset.Y + at.Y + opening.Offset);
                this.start = null;
            }
        }
        else if (!this.follow.Following && this.pane.Offset.Y < AnchoringStarts && this.top is { } first && TopLevel.GetTopLevel(first.Row) is not null
            && first.Row.TranslatePoint(default, this.Content()) is { } now && Math.Abs(now.Y - first.Y) >= 0.5)
            this.follow.Move(now.Y - first.Y);
        this.top = this.pane.Offset.Y < AnchoringStarts && this.TopRow() is { } shown && this.Container(shown.Row.Key) is { } container
            && container.TranslatePoint(default, this.Content()) is { } y ? (container, y.Y) : null;
        this.latest.IsVisible = !this.follow.AtLatest;
    }

    // Tell the owner where the pane rests: the first row whose bottom is below the top edge, and how far above the
    // edge that row starts.
    private void Report()
    {
        if (this.owner is not { } current) return;
        TranscriptAnchor? rest = null;
        if (!this.follow.Following && this.TopRow() is { } shown) rest = new TranscriptAnchor(shown.Row.Key, -shown.Top);
        current.Scrolled(rest);
    }

    // A push at the top asks for older messages until the content grows or none are left.
    private void Push()
    {
        if (this.owner is null || this.pane.Offset.Y >= AnchoringStarts) return;
        this.pushed ??= this.pane.Extent.Height;
        this.Evaluate();
    }

    // Asks the owner for the messages before the window, one load at a time: on a push, when the rows do not fill the
    // pane, or scrolled back with the first message in view within PrefetchMessages of the oldest loaded one. Anchoring
    // keeps the view in place when they arrive above it.
    private void Evaluate()
    {
        if (this.owner is not { } current || this.list is null || this.loading is not null || !this.pane.IsEffectivelyVisible) return;
        // A correction Place just made is laid out by the next pass, which evaluates again; the rows have not moved yet.
        if (this.pane.Presenter is { IsArrangeValid: false }) return;
        if (this.pushed is { } extent && this.pane.Extent.Height > extent + GrowthTolerance) this.pushed = null;
        bool push = this.pushed is not null;
        // A push asks again after a load that added nothing; every load waits for the owner to show the window it made.
        if (this.settled is { } last && last.Window == this.window && (last.Progressed || !push)) return;
        if (!push && !this.Short && !this.NearTop) return;
        _ = this.LoadOlderAsync(current, this.window);
    }

    // The rows do not fill the pane, so the person has nothing to scroll.
    private bool Short => this.pane.Viewport.Height > 0 && this.pane.Extent.Height <= this.pane.Viewport.Height;

    private bool NearTop => !this.follow.Following && this.TopRow() is { } shown && this.MessagesBefore(shown.Row) < PrefetchMessages;

    private void ForgetLoads()
    {
        this.loading?.Cancel();
        this.loading = null;
        this.settled = null;
        this.pushed = null;
    }

    // How many loaded messages come before the row: one per message row, and each message of a work stretch.
    private int MessagesBefore(TranscriptRow target)
    {
        int count = 0;
        foreach (TranscriptRow row in this.list?.Rows ?? [])
        {
            if (ReferenceEquals(row, target)) break;
            count += row.Kind == TranscriptRowKind.Activity ? row.Activity.Count : 1;
        }
        return count;
    }

    private async Task LoadOlderAsync(Owner current, (int Offset, int Count) from)
    {
        CancellationTokenSource load = this.loading = new();
        bool progressed = false;
        try { progressed = await current.LoadOlder(load.Token); }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            UiRegion.Log?.Invoke(new UiFailure("transcript-load-older", null, error));
        }
        finally
        {
            if (ReferenceEquals(this.loading, load)) this.loading = null;
            load.Dispose();
        }
        // A load the pane detached from, or that a new owner replaced, records nothing.
        if (load.IsCancellationRequested || !ReferenceEquals(current, this.owner)) return;
        this.settled = (from, progressed);
        if (!progressed) this.pushed = null;
    }

    private Visual Content() => this.pane.Content as Visual ?? this.pane;

    // The first row whose bottom is below the pane's top edge, and where its top sits relative to that edge.
    private (TranscriptRow Row, double Top)? TopRow()
    {
        (TranscriptRow Row, double Top)? best = null;
        if (this.list is not { } rows) return null;
        foreach (Control container in rows.Items.GetRealizedContainers())
        {
            if (container.DataContext is not TranscriptRow row) continue;
            if (container.TranslatePoint(default, this.pane) is not { } at || at.Y + container.Bounds.Height <= 0) continue;
            if (best is null || at.Y < best.Value.Top) best = (row, at.Y);
        }
        return best;
    }

    private Control? Container(string key)
    {
        IReadOnlyList<TranscriptRow> rows = this.list?.Rows ?? [];
        for (int index = 0; index < rows.Count; index++)
            if (rows[index].Key == key) return this.list!.Items.ContainerFromIndex(index);
        return null;
    }

    private sealed record Owner(string Key, Action<TranscriptAnchor?> Scrolled, Func<CancellationToken, Task<bool>> LoadOlder, Action Release);
}
