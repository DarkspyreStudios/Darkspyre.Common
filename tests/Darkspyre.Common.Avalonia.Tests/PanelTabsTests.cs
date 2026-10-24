using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class PanelTabsTests
{
    /// <summary>In a row narrower than its titles, the tabs stay on one line inside the row, the long title trims, and a click reports its tab.</summary>
    [Fact]
    public Task PanelTabsStayOnOneLineAndTrimWhenNarrow() => ViewSession.RunAsync(() =>
    {
        List<string> clicked = [];
        PanelTabs<string> row = new("Panels", clicked.Add);
        row.Show([new("prompts", "Prompts", false), new("files", "Files", false), new("git", "Git", true),
            new("remote", "Remote connections and their sessions", false)]);
        ViewSession.Show(row, width: 200, height: 80);

        ToggleButton[] tabs = [.. ViewSession.All<ToggleButton>(row)];
        Assert.Equal(4, tabs.Length);
        Assert.Single(tabs.Select(tab => tab.Bounds.Y).Distinct());
        Assert.All(tabs, tab => Assert.True(tab.Bounds.Right <= 200.5, $"{tab.Content} ends at {tab.Bounds.Right}."));
        TextBlock longTitle = ViewSession.All<TextBlock>(tabs[3]).Single();
        Assert.Contains(longTitle.TextLayout.TextLines, line => line.HasCollapsed);

        tabs[1].RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["files"], clicked);
        return Task.CompletedTask;
    });

    /// <summary>A tab whose title grows after the row is laid out, as the Tasks tab's count does, shows its whole new title.</summary>
    [Fact]
    public Task ATitleThatGrowsAfterLayoutShowsInFull() => ViewSession.RunAsync(() =>
    {
        PanelTabs<string> row = new("Panels", _ => { });
        row.Show([new("tasks", "Tasks", true), new("files", "Files", false), new("git", "Git", false), new("remote", "Remote", false)]);
        ViewSession.Show(row, width: 420, height: 80);

        row.Show([new("tasks", "Tasks 2", true), new("files", "Files", false), new("git", "Git", false), new("remote", "Remote", false)]);
        ViewSession.Settle();

        ToggleButton tasks = ViewSession.All<ToggleButton>(row).First();
        TextBlock title = ViewSession.All<TextBlock>(tasks).Single();
        Assert.Equal("Tasks 2", title.Text);
        Assert.DoesNotContain(title.TextLayout.TextLines, line => line.HasCollapsed);
        return Task.CompletedTask;
    });
}
