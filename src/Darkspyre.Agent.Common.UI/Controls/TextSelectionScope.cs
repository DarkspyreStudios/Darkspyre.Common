using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The root of one surface: it owns the selection of every text inside it that no inner scope owns.</summary>
/// <remarks>
/// A left press on text anchors a selection and a drag extends it across every text that joins, in reading order (the
/// depth-first order of the visual tree). Text joins unless it sits inside a control (a button, menu item, combo box, tab,
/// list or tree item, text box or tooltip), unless the nearest element with a <see cref="TextSelection.ModeProperty"/>
/// other than Auto says otherwise, and unless it is empty or not visible. A plain <see cref="TextBlock"/> is highlighted
/// by an adorner in <c>color-primary.brush.40</c>; a <see cref="SelectableTextBlock"/> through its own selection. A drag
/// held within 24 px of a scroll viewer's top or bottom edge scrolls it 20 px per move. A double click on plain text
/// selects a word, a triple click the whole text. The pointer is the text pointer over text that joins.
/// </remarks>
/// <gotcha>A window holds one selection: starting one in any scope clears the others in the same window. A scope is not
/// focusable on its own (<c>Focusable</c> stays false, with <c>IsTabStop = false</c> and <c>FocusAdorner = null</c>), so
/// Avalonia's own press-to-focus walk never lands on it: a press on empty space, padding, the header, the status bar or
/// the sidebar leaves a focused text box and its input-method composition as they are. Only a press that starts a
/// selection focuses the scope: it sets <c>Focusable = true</c>, calls <c>Focus</c>, and sets <c>Focusable</c> back to
/// false when the scope loses keyboard focus.</gotcha>
public sealed class TextSelectionScope : Decorator, IEditCommandTarget
{
    // The distance a click on a link may wander and stay a click.
    private const double DragThreshold = 4;
    // One line of body text at the default font size, and a step that keeps the selection end under the pointer.
    private const double EdgeZone = 24;
    private const double ScrollStep = 20;

    private static Cursor? textPointer;

    private readonly Dictionary<TextBlock, (int Start, int End)> ranges = new(ReferenceEqualityComparer.Instance);
    private List<TextBlock> leaves = [];
    private Dictionary<TextBlock, int> order = new(ReferenceEqualityComparer.Instance);
    private bool stale = true;
    private bool pressed;
    private bool dragging;
    private bool applying;
    private (TextBlock Leaf, int Index)? anchor;
    private Point pressPoint;
    private TopLevel? window;

    static TextSelectionScope()
    {
        IsTabStopProperty.OverrideDefaultValue<TextSelectionScope>(false);
        EditRouting.Install();
        SelectableTextBlock.SelectionStartProperty.Changed.AddClassHandler<SelectableTextBlock>((block, _) => SelectionMoved(block));
        SelectableTextBlock.SelectionEndProperty.Changed.AddClassHandler<SelectableTextBlock>((block, _) => SelectionMoved(block));
    }

    /// <summary>Creates a scope with no child.</summary>
    public TextSelectionScope()
    {
        this.FocusAdorner = null;
        this.AddHandler(PointerPressedEvent, this.Pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.AddHandler(PointerMovedEvent, this.Moved, RoutingStrategies.Bubble, handledEventsToo: true);
        this.AddHandler(PointerReleasedEvent, this.Released, RoutingStrategies.Bubble, handledEventsToo: true);
        this.LayoutUpdated += (_, _) => this.stale = true;
    }

    /// <summary>True while some text is selected.</summary>
    public bool CanCopy => this.ranges.Count > 0;

    /// <summary>The selected text, as shown, in reading order: a line break between two texts of one block, a blank line
    /// between blocks (<see cref="TextSelection.IsBlockProperty"/>). Markdown text gives its rendered words, without
    /// Markdown marks.</summary>
    public string SelectedText
    {
        get
        {
            if (this.ranges.Count == 0) return string.Empty;
            this.Refresh();
            StringBuilder text = new();
            Visual? lastBlock = null;
            bool first = true;
            foreach (TextBlock leaf in this.leaves)
            {
                if (!this.ranges.TryGetValue(leaf, out (int Start, int End) range)) continue;
                string piece = leaf is MarkdownTextBlock markdown ? markdown.ActualSelectedText : Slice(TextOf(leaf), range.Start, range.End);
                Visual? block = this.BlockOf(leaf);
                if (!first) text.Append(ReferenceEquals(block, lastBlock) ? "\n" : "\n\n");
                text.Append(piece);
                lastBlock = block;
                first = false;
            }
            return text.ToString();
        }
    }

    /// <summary>Raised when the selection changes, including when it clears.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Requests the copy of <see cref="SelectedText"/> through <see cref="TextCopy"/>.</summary>
    public void Copy()
    {
        string text = this.SelectedText;
        if (text.Length > 0) _ = TextCopy.RequestAsync(this, text);
    }

    /// <summary>Selects every text that joins.</summary>
    public void SelectAll()
    {
        this.Refresh();
        Dictionary<TextBlock, (int, int)> all = new(ReferenceEqualityComparer.Instance);
        foreach (TextBlock leaf in this.leaves) all[leaf] = (0, TextOf(leaf).Length);
        this.Apply(all);
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        this.anchor = null;
        if (this.ranges.Count > 0) this.Apply(new Dictionary<TextBlock, (int, int)>(ReferenceEqualityComparer.Instance));
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.window = TopLevel.GetTopLevel(this);
        this.stale = true;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (this.window is not null) TextSelection.Release(this.window, this);
        this.window = null;
        this.pressed = this.dragging = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!this.IsFocused) this.Focusable = false;
    }

    /// <summary>The text of <paramref name="block"/> as its selection indexes it: its inlines' text when it has inlines.</summary>
    internal static string TextOf(TextBlock block) => block.Inlines is { Count: > 0 } inlines ? inlines.Text ?? string.Empty : block.Text ?? string.Empty;

    private static string Slice(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        return text[start..end];
    }

    private static bool IsControl(Visual element) => element is Button or MenuItem or ComboBox or ComboBoxItem or TabItem or ListBoxItem or TreeViewItem
        or TextBox or ToolTip;

    private static TextSelectionScope? NearestScope(Visual visual) => visual.GetSelfAndVisualAncestors().OfType<TextSelectionScope>().FirstOrDefault();

    // A selectable text block that changed its own selection outside a press, such as clearing it when it lost focus,
    // gets the scope's range back.
    private static void SelectionMoved(SelectableTextBlock block)
    {
        if (NearestScope(block) is not { applying: false, pressed: false } scope || !scope.ranges.TryGetValue(block, out (int Start, int End) range)) return;
        if (Math.Min(block.SelectionStart, block.SelectionEnd) == range.Start && Math.Max(block.SelectionStart, block.SelectionEnd) == range.End) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (scope.pressed || !scope.ranges.TryGetValue(block, out (int Start, int End) now)) return;
            scope.applying = true;
            try
            {
                block.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, now.Start);
                block.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, now.End);
            }
            finally { scope.applying = false; }
        });
    }

    // True when a press on this element belongs to a control or a skipped container, so it starts no selection.
    private bool Blocked(Visual source)
    {
        for (Visual? element = source; element is not null && !ReferenceEquals(element, this); element = element.GetVisualParent())
            if (IsControl(element) || TextSelection.GetMode(element) == TextSelectionMode.Skip) return true;
        return false;
    }

    // Whether the text block joins this scope's selection: the nearest Skip or Include decides; else text inside a control
    // stays out.
    private bool Joins(TextBlock block)
    {
        if (!block.IsEffectivelyVisible || TextOf(block).Length == 0) return false;
        TextSelectionMode? decided = null;
        bool insideControl = false;
        for (Visual? element = block; element is not null && !ReferenceEquals(element, this); element = element.GetVisualParent())
        {
            TextSelectionMode mode = TextSelection.GetMode(element);
            if (decided is null && mode != TextSelectionMode.Auto) decided = mode;
            if (IsControl(element)) insideControl = true;
            if (!ReferenceEquals(element, block) && block is MarkdownTextBlock && element is MarkdownTextBlock) return false;
        }
        return decided is { } chosen ? chosen == TextSelectionMode.Include : !insideControl;
    }

    // The nearest container marked as one block, inside this scope.
    private Visual? BlockOf(TextBlock leaf)
    {
        for (Visual? element = leaf; element is not null && !ReferenceEquals(element, this); element = element.GetVisualParent())
            if (TextSelection.GetIsBlock(element)) return element;
        return null;
    }

    // Rebuilds the reading-order list of joining texts when the content changed, and drops the ranges of texts that left.
    private void Refresh()
    {
        if (!this.stale) return;
        this.stale = false;
        List<TextBlock> found = [];
        if (this.Child is { } child) this.Collect(child, found);
        this.leaves = found;
        this.order = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < found.Count; index++) this.order[found[index]] = index;
        List<TextBlock> gone = [.. this.ranges.Keys.Where(leaf => !this.order.ContainsKey(leaf))];
        foreach (TextBlock leaf in gone)
        {
            this.ranges.Remove(leaf);
            Show(leaf, 0, 0);
        }
        if (gone.Count > 0) this.Changed();
    }

    private void Collect(Visual visual, List<TextBlock> found)
    {
        if (!visual.IsVisible || visual is TextSelectionScope) return;
        if (visual is TextBlock block && this.Joins(block)) found.Add(block);
        foreach (Visual child in visual.GetVisualChildren()) this.Collect(child, found);
    }

    // The text's bounds in this scope's coordinates, render transforms included.
    private Rect BoundsOf(TextBlock leaf) =>
        leaf.TransformToVisual(this) is { } transform ? new Rect(leaf.Bounds.Size).TransformToAABB(transform) : default;

    // The joining text under the point by geometry, or null.
    private TextBlock? LeafAt(Point point) => this.leaves.FirstOrDefault(leaf => this.BoundsOf(leaf).Contains(point));

    // The text the drag's end is in: the one under the point; else the last one whose top is above the point; else the first.
    private TextBlock? FocusLeaf(Point point)
    {
        if (this.LeafAt(point) is { } under) return under;
        return this.leaves.LastOrDefault(leaf => this.BoundsOf(leaf).Top <= point.Y) ?? this.leaves.FirstOrDefault();
    }

    // The character position of the point in the text: before its start above it, after its end below it, else the hit.
    private int IndexIn(TextBlock leaf, Point point)
    {
        int length = TextOf(leaf).Length;
        if (this.TranslatePoint(point, leaf) is not { } local) return 0;
        if (local.Y < 0) return 0;
        if (local.Y >= leaf.Bounds.Height) return length;
        Thickness padding = leaf.Padding;
        Point inner = new(Math.Max(0, local.X - padding.Left), local.Y - padding.Top);
        return Math.Clamp(leaf.TextLayout.HitTestPoint(inner).TextPosition, 0, length);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !ReferenceEquals(NearestScope(source), this)) return;
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || this.Blocked(source)) return;
        if (this.window is not null) TextSelection.ClearWindow(this.window);
        this.ClearSelection();
        this.pressed = true;
        this.dragging = false;
        this.stale = true;
        this.Refresh();
        if (this.LeafAt(point.Position) is not { } leaf) return;
        int index = this.IndexIn(leaf, point.Position);
        this.anchor = (leaf, index);
        this.pressPoint = point.Position;
        if (leaf is not SelectableTextBlock && e.ClickCount >= 2)
        {
            string text = TextOf(leaf);
            (int start, int end) = e.ClickCount == 2 ? Word(text, index) : (0, text.Length);
            this.Apply(new Dictionary<TextBlock, (int, int)>(ReferenceEqualityComparer.Instance) { [leaf] = (start, end) });
        }
        this.Focusable = true;
        this.Focus(NavigationMethod.Pointer);
    }

    // The word around the index: a run of letters, digits and underscores, or the single other character there.
    private static (int Start, int End) Word(string text, int index)
    {
        if (text.Length == 0) return (0, 0);
        int at = Math.Clamp(index, 0, text.Length - 1);
        static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';
        if (!IsWord(text[at])) return (at, at + 1);
        int start = at, end = at;
        while (start > 0 && IsWord(text[start - 1])) start--;
        while (end < text.Length && IsWord(text[end])) end++;
        return (start, end);
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            this.pressed = this.dragging = false;
            this.ShowPointer(e);
            return;
        }
        if (!this.pressed || this.anchor is not { } start) return;
        Point position = point.Position;
        if (!this.dragging)
        {
            Vector moved = position - this.pressPoint;
            if (Math.Sqrt((moved.X * moved.X) + (moved.Y * moved.Y)) < DragThreshold) return;
            this.dragging = true;
            if (e.Pointer.Captured is not Visual held || !this.IsVisualAncestorOf(held)) e.Pointer.Capture(this);
        }
        this.Refresh();
        if (!this.order.TryGetValue(start.Leaf, out int from) || this.FocusLeaf(position) is not { } end) return;
        int to = this.order[end];
        int endIndex = this.IndexIn(end, position);
        Dictionary<TextBlock, (int, int)> next = new(ReferenceEqualityComparer.Instance);
        if (from == to) next[end] = (Math.Min(start.Index, endIndex), Math.Max(start.Index, endIndex));
        else
        {
            (int first, int last) = from < to ? (from, to) : (to, from);
            for (int index = first + 1; index < last; index++) next[this.leaves[index]] = (0, TextOf(this.leaves[index]).Length);
            if (from < to)
            {
                next[start.Leaf] = (start.Index, TextOf(start.Leaf).Length);
                next[end] = (0, endIndex);
            }
            else
            {
                next[start.Leaf] = (0, start.Index);
                next[end] = (endIndex, TextOf(end).Length);
            }
        }
        this.Apply(next);
        this.ScrollNear(start.Leaf, position);
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || !this.pressed) return;
        this.pressed = false;
        if (this.dragging)
        {
            this.dragging = false;
            if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
            return;
        }
        // A click on a selectable text block may have selected a word or its whole text itself.
        if (this.anchor is { Leaf: SelectableTextBlock block } && block.SelectionStart != block.SelectionEnd)
            this.Apply(new Dictionary<TextBlock, (int, int)>(ReferenceEqualityComparer.Instance)
            {
                [block] = (Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd)),
            });
    }

    // The text pointer while the pointer is over text that joins; the control's own pointer elsewhere.
    private void ShowPointer(PointerEventArgs e)
    {
        bool overText = e.Source is TextBlock block && ReferenceEquals(NearestScope(block), this) && !this.Blocked(block) && this.Joins(block);
        if (overText) this.Cursor = textPointer ??= new Cursor(StandardCursorType.Ibeam);
        else if (this.IsSet(CursorProperty)) this.ClearValue(CursorProperty);
    }

    // A drag held near the top or bottom edge of the scroll viewer the anchor sits in scrolls it toward that edge.
    private void ScrollNear(TextBlock leaf, Point position)
    {
        ScrollViewer? viewer = leaf.GetVisualAncestors().TakeWhile(element => !ReferenceEquals(element, this)).OfType<ScrollViewer>()
            .FirstOrDefault(found => found.Extent.Height > found.Viewport.Height);
        if (viewer is null || this.TranslatePoint(position, viewer) is not { } inside) return;
        double step = inside.Y < EdgeZone ? -ScrollStep : inside.Y > viewer.Bounds.Height - EdgeZone ? ScrollStep : 0;
        if (step == 0) return;
        double top = Math.Clamp(viewer.Offset.Y + step, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));
        viewer.Offset = new Vector(viewer.Offset.X, top);
    }

    // Shows the new ranges, touching only the texts whose range changed, and takes or gives up the window's selection.
    private void Apply(Dictionary<TextBlock, (int Start, int End)> next)
    {
        foreach (KeyValuePair<TextBlock, (int Start, int End)> entry in next.Where(entry => entry.Value.End <= entry.Value.Start).ToList()) next.Remove(entry.Key);
        bool changed = false;
        this.applying = true;
        try
        {
            foreach (TextBlock leaf in this.ranges.Keys.Where(leaf => !next.ContainsKey(leaf)).ToList())
            {
                this.ranges.Remove(leaf);
                Show(leaf, 0, 0);
                changed = true;
            }
            foreach ((TextBlock leaf, (int start, int end)) in next)
            {
                if (this.ranges.TryGetValue(leaf, out (int Start, int End) shown) && shown == (start, end)) continue;
                this.ranges[leaf] = (start, end);
                Show(leaf, start, end);
                changed = true;
            }
        }
        finally { this.applying = false; }
        if (!changed) return;
        if (this.window is not null)
        {
            if (this.ranges.Count > 0) TextSelection.Take(this.window, this);
            else TextSelection.Release(this.window, this);
        }
        this.Changed();
    }

    private void Changed() => this.SelectionChanged?.Invoke(this, EventArgs.Empty);

    private static void Show(TextBlock leaf, int start, int end)
    {
        if (leaf is SelectableTextBlock selectable)
        {
            selectable.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, start);
            selectable.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, end);
        }
        else TextHighlight.Show(leaf, start, end);
    }
}
