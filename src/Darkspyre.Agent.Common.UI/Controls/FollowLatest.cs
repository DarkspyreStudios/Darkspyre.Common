using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// Keeps a <see cref="ScrollViewer"/> locked to its newest content at the bottom. While it follows, every layout pass that
/// leaves the pane above its bottom moves it to the bottom. Only the person's scrolling changes whether it follows:
/// scrolling away from the bottom stops following, and scrolling back to the bottom follows again. The person's scrolling
/// is the mouse wheel or a trackpad, a key, a scroll gesture, a scroll bar while it is held, and anything that moves the
/// pane while a left press inside it is held, such as a drag that selects text past its edge. An offset set through
/// <see cref="Move"/>, the pane's scroll anchoring and layout clamping the offset never change whether it follows.
/// </summary>
/// <remarks>
/// Every move to the bottom runs in <see cref="ScrollViewer.ScrollChanged"/>, which the pane raises at the end of a layout
/// pass; an offset set there is laid out again before the frame is drawn. A correction posted to the dispatcher at
/// Loaded priority draws one frame at the wrong place first.
/// </remarks>
/// <gotcha>
/// It follows from the moment it is created. An owner that opens the pane somewhere else calls <see cref="Open"/> with
/// false before the first layout pass. Every member runs on the UI thread.
/// </gotcha>
public sealed class FollowLatest
{
    // How far from the bottom, in pixels, still counts as the bottom: a trackpad leaves fractions of a pixel.
    private const double BottomTolerance = 1;
    private readonly ScrollViewer pane;
    private bool following = true;
    // The owner is moving the pane, so the offset it sets is not the person's.
    private bool moving;
    // The person is turning the wheel, pressing a key, holding a press in the pane or panning over the pane.
    private bool input;
    // The person moved the pane since the last layout pass.
    private bool personMoved;

    /// <summary>Drives <paramref name="pane"/> from now on. It starts following.</summary>
    /// <param name="pane">The scrolling pane whose newest content is at its bottom.</param>
    public FollowLatest(ScrollViewer pane)
    {
        this.pane = pane;
        pane.PropertyChanged += (_, args) =>
        {
            if (args.Property == ScrollViewer.OffsetProperty) this.OnOffsetChanged();
        };
        pane.ScrollChanged += (_, _) => this.OnScrollChanged();
        // The person's scrolling: the wheel or a trackpad, the keyboard, a pan, a scroll bar while it is held, and a held
        // left press, whose drag scrolls the pane past its edge while it selects text.
        Input(InputElement.PointerWheelChangedEvent, InputElement.PointerWheelChangedEvent);
        Input(InputElement.KeyDownEvent, InputElement.KeyDownEvent);
        Input(InputElement.ScrollGestureEvent, InputElement.ScrollGestureEndedEvent);
        pane.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (args.GetCurrentPoint(pane).Properties.IsLeftButtonPressed
                || (args.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null))
                this.input = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.PointerReleasedEvent, (_, _) => this.input = false, RoutingStrategies.Bubble, handledEventsToo: true);
        pane.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => this.input = false, RoutingStrategies.Bubble, handledEventsToo: true);

        // The pane's own handling of the event runs between the event tunnelling down and bubbling back up.
        void Input(RoutedEvent begins, RoutedEvent ends)
        {
            pane.AddHandler(begins, new EventHandler<RoutedEventArgs>((_, _) => this.input = true), RoutingStrategies.Tunnel, handledEventsToo: true);
            pane.AddHandler(ends, new EventHandler<RoutedEventArgs>((_, _) => this.input = false), RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    /// <summary>Whether the pane follows its newest content.</summary>
    public bool Following => this.following;

    /// <summary>Whether the pane shows its bottom edge, within one pixel.</summary>
    public bool AtLatest => this.pane.Extent.Height - this.pane.Offset.Y - this.pane.Viewport.Height < BottomTolerance;

    /// <summary>
    /// Raised when the pane starts following because the person scrolled to the bottom or <see cref="Latest"/> ran while
    /// it was not following. <see cref="Open"/> never raises it.
    /// </summary>
    public event EventHandler? Locked;

    /// <summary>
    /// Raised each time the person's scrolling moves the pane, after <see cref="Following"/> reflects the move.
    /// </summary>
    public event EventHandler? Scrolled;

    /// <summary>Follows again and moves the pane to the bottom.</summary>
    public void Latest()
    {
        this.Lock();
        if (!this.AtLatest) this.ToEnd();
    }

    /// <summary>
    /// Sets whether the pane follows, as when its owner opens it on new content, without raising <see cref="Locked"/>.
    /// Following moves the pane to the bottom.
    /// </summary>
    /// <param name="follow">True to follow the newest content; false to leave the pane where the owner puts it.</param>
    public void Open(bool follow)
    {
        this.following = follow;
        this.personMoved = false;
        if (follow && !this.AtLatest) this.ToEnd();
    }

    /// <summary>Sets the pane's vertical offset as the owner's own move, which never counts as the person's scrolling.</summary>
    /// <param name="offsetY">The new vertical offset in pixels.</param>
    public void Move(double offsetY)
    {
        this.moving = true;
        try { this.pane.Offset = this.pane.Offset.WithY(offsetY); }
        finally { this.moving = false; }
    }

    // The offset changes when the person scrolls, when the owner moves the pane, when anchoring holds a row in view, and
    // when layout clamps it to content that shrank. Only a change made while the person scrolls is theirs.
    private void OnOffsetChanged()
    {
        if (this.moving || !this.input) return;
        this.personMoved = true;
        // Scrolling away from the bottom stops following; reaching it follows again once the extent is laid out.
        if (!this.AtLatest) this.following = false;
        this.Scrolled?.Invoke(this, EventArgs.Empty);
    }

    // Raised at the end of every layout pass that changed the extent, the viewport or the offset.
    private void OnScrollChanged()
    {
        if (this.personMoved)
        {
            this.personMoved = false;
            if (this.AtLatest) this.Lock();
        }
        if (this.following && !this.AtLatest) this.ToEnd();
    }

    // Follows the newest content; arriving there from elsewhere tells the owner.
    private void Lock()
    {
        bool arrived = !this.following;
        this.following = true;
        if (arrived) this.Locked?.Invoke(this, EventArgs.Empty);
    }

    private void ToEnd()
    {
        this.moving = true;
        try { this.pane.ScrollToEnd(); }
        finally { this.moving = false; }
    }
}
