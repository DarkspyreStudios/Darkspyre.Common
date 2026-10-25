using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class PanelPartsTests
{
    /// <summary>
    /// The panel parts take their sizes from a section's text entries: 24 px controls at the default entries, each part's
    /// entry when the entries are raised, and controls that grow past 24 px with large text.
    /// </summary>
    [Fact]
    public Task PanelPartsFollowTheSectionsTextEntries() => ViewSession.RunAsync(() =>
    {
        Parts standard = new(title: 13, secondary: 12, small: 11);
        Parts raised = new(title: 16, secondary: 15, small: 14);
        // The headless test font's line is as tall as its size, so 26 px text is the first size well past a 24 px box.
        Parts large = new(title: 26, secondary: 12, small: 11);
        ViewSession.Show(new StackPanel { Children = { standard.Host, raised.Host, large.Host } }, width: 460, height: 900);

        Assert.Equal(24, standard.Selector.Bounds.Height, 0.5);
        Assert.Equal(24, standard.Search.Bounds.Height, 0.5);
        Assert.Equal(24, standard.Button.Bounds.Height, 0.5);

        Assert.Equal(16, raised.Row.FontSize);
        Assert.Equal(16, raised.Name.FontSize);
        Assert.Equal(15, raised.Detail.FontSize);
        Assert.Equal(14, raised.SectionTitle.FontSize);
        Assert.Equal(15, raised.SecondTab.FontSize);
        Assert.Equal(16, raised.Button.FontSize);

        Assert.True(large.Selector.Bounds.Height > 24.5, $"The selector stays {large.Selector.Bounds.Height} px high.");
        Assert.True(large.Button.Bounds.Height > 24.5, $"The text button stays {large.Button.Bounds.Height} px high.");
        return Task.CompletedTask;
    });

    // One set of parts inside a host whose resources hold a section's text entries, as a section's dictionary does.
    private sealed class Parts
    {
        public Parts(double title, double secondary, double small)
        {
            this.Selector.Classes.Add("panel-selector");
            this.Selector.Items.Add("agent-ds");
            this.Selector.SelectedIndex = 0;
            this.Search.Classes.Add("panel-search");
            this.Row.Content = new DockPanel { Children = { this.Detail, this.Name } };
            this.Name.Text = "settings-and-sessions.md";
            this.Detail.Text = "docs";
            DockPanel header = PanelParts.Section(out TextBlock sectionTitle, out TextBlock count);
            PanelParts.ShowSection(header, sectionTitle, count, "Changes", 2);
            this.SectionTitle = sectionTitle;
            PanelTabs<string> second = new("Git views", _ => { });
            second.Classes.Add("second");
            second.Show([new("changes", "Changes", true)]);
            this.SecondTab = (ToggleButton)((Panel)second.Content!).Children[0];
            this.Host = new StackPanel { Children = { this.Selector, this.Search, this.Button, this.Row, header, second } };
            this.Host.Resources["control-h-xs"] = 24d;
            this.Host.Resources["font-size-title"] = title;
            this.Host.Resources["font-size-secondary"] = secondary;
            this.Host.Resources["font-size-small"] = small;
        }

        public StackPanel Host { get; }

        public ComboBox Selector { get; } = new();

        public TextBox Search { get; } = new() { Text = "Search changes" };

        public Button Button { get; } = PanelParts.TextButton("Fetch", "secondary", () => Task.CompletedTask);

        public Button Row { get; } = PanelParts.Row();

        public TextBlock Name { get; } = PanelParts.Name();

        public TextBlock Detail { get; } = PanelParts.Detail();

        public TextBlock SectionTitle { get; }

        public ToggleButton SecondTab { get; }
    }
}
