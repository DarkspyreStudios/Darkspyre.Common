using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>One tab of a <see cref="PanelTabs{TKey}"/> row.</summary>
/// <param name="Key">The tab's identity, unique in its row.</param>
/// <param name="Title">The tab's text. It trims with an ellipsis when the row is narrow and shows in full on hover.</param>
/// <param name="Checked">Whether this is the selected tab. At most one tab of a row is checked.</param>
public sealed record PanelTab<TKey>(TKey Key, string Title, bool Checked) where TKey : notnull;

/// <summary>
/// A row of panel tabs on one line: the side panel's tab row and a panel's own second row. Each tab is a
/// <c>ToggleButton.plugin-panel-tab</c> whose content is its title. When the titles do not fit, a tab narrower than an
/// equal share keeps its width and the others share the rest, so the row never wraps and long titles trim. A title that
/// changes after layout lays the row out again, so a tab that fits shows its whole new title. A click
/// shows the clicked tab checked and calls the selection callback, also for the tab already checked. A panel's second
/// row adds the class <c>second</c>, which sets the smaller second-row size.
/// </summary>
/// <gotcha>The owner decides which tab is checked: each <see cref="Show"/> sets every tab from its record, so a click the
/// owner does not follow is undone by the next Show.</gotcha>
public sealed class PanelTabs<TKey> : UserControl where TKey : notnull
{
    private readonly OneLinePanel row = new();
    private readonly KeyedViews<TKey, PanelTab<TKey>, ToggleButton> tabs;
    private readonly Action<TKey> selected;

    /// <summary>Creates an empty row named <paramref name="automationName"/>; <paramref name="selected"/> runs on each click with the tab's key.</summary>
    public PanelTabs(string automationName, Action<TKey> selected)
    {
        this.selected = selected;
        this.tabs = new(tab => tab.Key, this.Create, static (button, tab) => Apply(button, tab));
        this.Classes.Add("plugin-panel-tabs");
        AutomationProperties.SetName(this, automationName);
        this.Content = this.row;
    }

    /// <summary>Shows <paramref name="tabs"/> in order. A tab whose record is unchanged keeps its control untouched.</summary>
    /// <exception cref="ArgumentException">Two tabs have the same key.</exception>
    public void Show(IReadOnlyList<PanelTab<TKey>> tabs)
    {
        this.tabs.Apply(this.row.Children, tabs);
        // A click checks its tab before the owner answers, so every tab is set from its record, changed or not.
        foreach (PanelTab<TKey> tab in tabs) this.tabs.Find(tab.Key)!.IsChecked = tab.Checked;
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
