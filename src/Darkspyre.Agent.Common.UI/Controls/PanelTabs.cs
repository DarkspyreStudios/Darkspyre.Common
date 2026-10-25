using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>One tab of a <see cref="PanelTabs{TKey}"/> row.</summary>
/// <param name="Key">The tab's identity, unique in its row.</param>
/// <param name="Title">The tab's text. It trims with an ellipsis when a Trim row is narrow and shows in full on hover.</param>
/// <param name="Checked">Whether this is the selected tab. At most one tab of a row is checked.</param>
public sealed record PanelTab<TKey>(TKey Key, string Title, bool Checked) where TKey : notnull
{
    /// <summary>Whether the tab takes clicks. A disabled tab shows in the faint colour; default true.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>What a <see cref="PanelTabs{TKey}"/> row does when its titles are wider than the row.</summary>
public enum PanelTabsOverflow
{
    /// <summary>The row keeps one line and long titles trim: a panel's second row.</summary>
    Trim,

    /// <summary>Every title stays whole and the row scrolls sideways.</summary>
    Scroll,
}

/// <summary>
/// A row of panel tabs on one line: the side panel's tab row and a panel's own second row. Each tab is a
/// <c>ToggleButton.plugin-panel-tab</c> whose content is its title. A click shows the clicked tab checked and calls the
/// selection callback, also for the tab already checked; a disabled tab takes no click. A panel's second row adds the
/// class <c>second</c>, which sets the smaller second-row size.
/// <para>
/// A Trim row: when the titles do not fit, a tab narrower than an equal share keeps its width and the others share the
/// rest, so the row never wraps and long titles trim. A title that changes after layout lays the row out again, so a tab
/// that fits shows its whole new title.
/// </para>
/// <para>
/// A Scroll row carries the class <c>scroll</c>. Every tab keeps its whole title and the row scrolls sideways, by a
/// trackpad's sideways scroll or by the mouse wheel; a newly checked tab scrolls into view.
/// </para>
/// <para>A row with no tab keeps one tab's height, so the views under it do not move.</para>
/// </summary>
/// <gotcha>The owner decides which tab is checked: each <see cref="Show"/> sets every tab from its record, so a click the
/// owner does not follow is undone by the next Show.</gotcha>
public sealed class PanelTabs<TKey> : UserControl where TKey : notnull
{
    // How far one wheel notch moves a Scroll row: about one short tab.
    private const double WheelStep = 50;

    private readonly Panel row;
    private readonly ScrollViewer? scroller;
    private readonly KeyedViews<TKey, PanelTab<TKey>, ToggleButton> tabs;
    private readonly Action<TKey> selected;
    private readonly ToggleButton placeholder = Placeholder();
    private TKey? shownChecked;
    private bool anyShownChecked;

    /// <summary>Creates an empty row named <paramref name="automationName"/> that trims; <paramref name="selected"/> runs on each click with the tab's key.</summary>
    public PanelTabs(string automationName, Action<TKey> selected)
        : this(automationName, selected, PanelTabsOverflow.Trim)
    {
    }

    /// <summary>Creates an empty row named <paramref name="automationName"/> that handles overflow as <paramref name="overflow"/> says; <paramref name="selected"/> runs on each click with the tab's key.</summary>
    public PanelTabs(string automationName, Action<TKey> selected, PanelTabsOverflow overflow)
    {
        this.selected = selected;
        this.tabs = new(tab => tab.Key, this.Create, static (button, tab) => Apply(button, tab));
        this.Classes.Add("plugin-panel-tabs");
        AutomationProperties.SetName(this, automationName);
        if (overflow == PanelTabsOverflow.Scroll)
        {
            this.Classes.Add("scroll");
            this.row = new StackPanel { Orientation = Orientation.Horizontal };
            this.scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = this.row,
            };
            this.scroller.AddHandler(PointerWheelChangedEvent, this.Wheel, RoutingStrategies.Tunnel);
            this.Content = this.scroller;
        }
        else
        {
            this.row = new OneLinePanel();
            this.Content = this.row;
        }
        this.row.Children.Add(this.placeholder);
    }

    /// <summary>
    /// Shows <paramref name="tabs"/> in order. A tab whose record is unchanged keeps its control untouched. In a Scroll
    /// row, a checked tab other than the one last shown checked scrolls into view.
    /// </summary>
    /// <exception cref="ArgumentException">Two tabs have the same key.</exception>
    public void Show(IReadOnlyList<PanelTab<TKey>> tabs)
    {
        this.tabs.Apply(this.row.Children, tabs);
        // An empty row holds the invisible placeholder tab, so it keeps one tab's height at any font size.
        if (tabs.Count == 0) this.row.Children.Add(this.placeholder);
        // A click checks its tab before the owner answers, so every tab is set from its record, changed or not.
        foreach (PanelTab<TKey> tab in tabs) this.tabs.Find(tab.Key)!.IsChecked = tab.Checked;
        PanelTab<TKey>? checkedTab = tabs.FirstOrDefault(tab => tab.Checked);
        bool changed = checkedTab is null
            ? this.anyShownChecked
            : !this.anyShownChecked || !EqualityComparer<TKey>.Default.Equals(this.shownChecked, checkedTab.Key);
        this.anyShownChecked = checkedTab is not null;
        this.shownChecked = checkedTab is null ? default : checkedTab.Key;
        if (changed) this.BringCheckedIntoView();
    }

    /// <summary>
    /// Scrolls a Scroll row so its checked tab is in view, after the next layout. A Trim row, or a row with no checked tab,
    /// does nothing.
    /// </summary>
    public void BringCheckedIntoView()
    {
        if (this.scroller is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            ToggleButton? checkedButton = this.row.Children.OfType<ToggleButton>()
                .FirstOrDefault(button => button != this.placeholder && button.IsChecked == true);
            checkedButton?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    // A vertical wheel moves a Scroll row sideways when its tabs are wider than it; a sideways scroll is the viewer's own.
    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        ScrollViewer viewer = this.scroller!;
        double room = viewer.Extent.Width - viewer.Viewport.Width;
        if (e.Delta.X != 0 || e.Delta.Y == 0 || room <= 0) return;
        viewer.Offset = new Vector(Math.Clamp(viewer.Offset.X - e.Delta.Y * WheelStep, 0, room), viewer.Offset.Y);
        e.Handled = true;
    }

    private ToggleButton Create(PanelTab<TKey> tab)
    {
        ToggleButton button = new();
        button.Classes.Add("plugin-panel-tab");
        Apply(button, tab);
        TKey key = tab.Key;
        button.Click += (_, _) =>
        {
            // A click selects the tab, so a click on the checked tab leaves it checked.
            button.IsChecked = true;
            this.selected(key);
        };
        return button;
    }

    private static void Apply(ToggleButton button, PanelTab<TKey> tab)
    {
        button.Content = tab.Title;
        ToolTip.SetTip(button, tab.Title);
        AutomationProperties.SetName(button, tab.Title);
        button.IsChecked = tab.Checked;
        button.IsEnabled = tab.Enabled;
    }

    // The empty row's stand-in: a tab's height, never seen, clicked, focused or announced.
    private static ToggleButton Placeholder()
    {
        ToggleButton button = new() { Content = "Preview", Focusable = false };
        button.Classes.Add("plugin-panel-tab");
        button.Classes.Add("placeholder");
        AutomationProperties.SetAccessibilityView(button, AccessibilityView.Raw);
        return button;
    }

    /// <summary>
    /// Lays its children out left to right on one line. When their natural widths do not fit, a child at or under an
    /// equal share of the width keeps its natural width and the rest split what is left equally, repeated until stable.
    /// </summary>
    private sealed class OneLinePanel : Panel
    {
        private double[] widths = [];

        protected override Size MeasureOverride(Size availableSize)
        {
            int count = this.Children.Count;
            double[] natural = new double[count];
            double height = 0;
            for (int index = 0; index < count; index++)
            {
                this.Children[index].Measure(new Size(double.PositiveInfinity, availableSize.Height));
                natural[index] = this.Children[index].DesiredSize.Width;
            }
            this.widths = Share(natural, availableSize.Width);
            for (int index = 0; index < count; index++)
            {
                // Only a trimmed child is measured again at its share. A child that fits keeps its unbounded measure,
                // so a title that grows later changes its desired width and the row lays out again.
                if (this.widths[index] < natural[index])
                    this.Children[index].Measure(new Size(this.widths[index], availableSize.Height));
                height = Math.Max(height, this.Children[index].DesiredSize.Height);
            }
            return new Size(this.widths.Sum(), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (this.widths.Length != this.Children.Count || this.widths.Sum() > finalSize.Width + 0.5)
                this.widths = Share([.. this.Children.Select(child => child.DesiredSize.Width)], finalSize.Width);
            double x = 0;
            for (int index = 0; index < this.Children.Count; index++)
            {
                this.Children[index].Arrange(new Rect(x, 0, this.widths[index], finalSize.Height));
                x += this.widths[index];
            }
            return finalSize;
        }

        // Natural widths when they fit; otherwise the narrow ones keep theirs and the others share the rest equally.
        private static double[] Share(double[] natural, double width)
        {
            double[] shared = [.. natural];
            if (double.IsInfinity(width) || natural.Sum() <= width) return shared;
            List<int> open = [.. Enumerable.Range(0, natural.Length)];
            double left = width;
            while (open.Count > 0)
            {
                double share = left / open.Count;
                int[] fitting = [.. open.Where(index => natural[index] <= share)];
                if (fitting.Length == 0)
                {
                    foreach (int index in open) shared[index] = Math.Max(0, share);
                    break;
                }
                foreach (int index in fitting)
                {
                    left -= natural[index];
                    open.Remove(index);
                }
            }
            return shared;
        }
    }
}
