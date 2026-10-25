using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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

    /// <summary>Eight tabs in a 460 px Scroll row keep their whole titles, and checking the last one scrolls it into view.</summary>
    [Fact]
    public Task AScrollRowKeepsEveryTitleWholeAndScrollsTheCheckedTabIntoView() => ViewSession.RunAsync(() =>
    {
        PanelTabs<int> row = new("Panels", _ => { }, PanelTabsOverflow.Scroll);
        row.Show(DockTabs(checkedIndex: 0));
        ViewSession.Show(row, width: 460, height: 80);

        ScrollViewer viewer = ViewSession.All<ScrollViewer>(row).Single();
        ToggleButton[] tabs = [.. ViewSession.All<ToggleButton>(row)];
        Assert.Equal(8, tabs.Length);
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width, $"The tabs ({viewer.Extent.Width}) fit the row ({viewer.Viewport.Width}).");
        Assert.All(tabs, tab => Assert.DoesNotContain(ViewSession.All<TextBlock>(tab).Single().TextLayout.TextLines, line => line.HasCollapsed));
        Assert.False(InView(tabs[7], viewer), "The last tab starts in view.");

        row.Show(DockTabs(checkedIndex: 7));
        ViewSession.Settle();

        Assert.True(InView(tabs[7], viewer), $"The checked tab is out of view at offset {viewer.Offset.X}.");
        return Task.CompletedTask;
    });

    /// <summary>A vertical wheel turn over a Scroll row wider than its tabs' room moves it sideways.</summary>
    [Fact]
    public Task AWheelOverAScrollRowMovesItSideways() => ViewSession.RunAsync(() =>
    {
        PanelTabs<int> row = new("Panels", _ => { }, PanelTabsOverflow.Scroll);
        row.Show(DockTabs(checkedIndex: 0));
        Window window = ViewSession.Show(row, width: 460, height: 80);
        ScrollViewer viewer = ViewSession.All<ScrollViewer>(row).Single();
        Assert.Equal(0, viewer.Offset.X);

        window.MouseWheel(new Point(100, row.Bounds.Height / 2), new Vector(0, -1));
        ViewSession.Settle();

        Assert.True(viewer.Offset.X > 0, "The wheel left the row in place.");
        return Task.CompletedTask;
    });

    /// <summary>A disabled tab is not enabled and a click on it does not reach the callback; an enabled tab's click does.</summary>
    [Fact]
    public Task ADisabledTabTakesNoClick() => ViewSession.RunAsync(() =>
    {
        List<string> clicked = [];
        PanelTabs<string> row = new("Remote views", clicked.Add);
        row.Show([new("session", "Session", true), new("configure", "Configure", false) { Enabled = false }]);
        Window window = ViewSession.Show(row, width: 460, height: 80);
        ToggleButton[] tabs = [.. ViewSession.All<ToggleButton>(row)];
        Assert.False(tabs[1].IsEnabled);

        Click(window, tabs[1]);
        Click(window, tabs[0]);

        Assert.Equal(["session"], clicked);
        return Task.CompletedTask;
    });

    /// <summary>An empty row is as tall as a row with one tab, at two text sizes.</summary>
    [Fact]
    public Task AnEmptyRowKeepsOneTabsHeight() => ViewSession.RunAsync(() =>
    {
        (PanelTabs<string> Empty, PanelTabs<string> One)[] sizes = [Rows(), Rows()];
        StackPanel small = new() { Children = { sizes[0].Empty, sizes[0].One } };
        small.Resources["font-size-secondary"] = 12d;
        StackPanel large = new() { Children = { sizes[1].Empty, sizes[1].One } };
        large.Resources["font-size-secondary"] = 22d;
        ViewSession.Show(new StackPanel { Children = { small, large } }, width: 460, height: 300);

        foreach ((PanelTabs<string> empty, PanelTabs<string> one) in sizes)
        {
            Assert.True(one.Bounds.Height > 1, "The row with one tab has no height.");
            Assert.Equal(one.Bounds.Height, empty.Bounds.Height, 0.5);
        }
        Assert.True(sizes[1].Empty.Bounds.Height > sizes[0].Empty.Bounds.Height, "The larger text left the empty row's height unchanged.");
        return Task.CompletedTask;

        static (PanelTabs<string> Empty, PanelTabs<string> One) Rows()
        {
            PanelTabs<string> empty = new("Files views", _ => { });
            empty.Classes.Add("second");
            empty.Show([]);
            PanelTabs<string> one = new("Files views", _ => { });
            one.Classes.Add("second");
            one.Show([new("source", "Source", true)]);
            return (empty, one);
        }
    });

    // The right dock's eight panels.
    private static readonly string[] DockTitles = ["Prompts", "Workers", "Tasks 6", "Files", "Git", "Browser", "Build Output", "Remote connections"];

    // The right dock's eight panels, the one at checkedIndex checked.
    private static PanelTab<int>[] DockTabs(int checkedIndex) =>
        [.. DockTitles.Select((title, index) => new PanelTab<int>(index, title, index == checkedIndex))];

    // Whether the whole tab lies inside the viewer's visible width.
    private static bool InView(ToggleButton tab, ScrollViewer viewer)
    {
        Point left = tab.TranslatePoint(default, viewer) ?? throw new InvalidOperationException("The tab is not under the viewer.");
        return left.X >= -0.5 && left.X + tab.Bounds.Width <= viewer.Viewport.Width + 0.5;
    }

    // A left click at the tab's centre, through the window's input.
    private static void Click(Window window, Control target)
    {
        Point centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control is not in the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        ViewSession.Settle();
    }
}
