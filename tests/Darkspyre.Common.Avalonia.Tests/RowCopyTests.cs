using Avalonia.Controls;
using Avalonia.Interactivity;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class RowCopyTests
{
    /// <summary>
    /// The Copy items go first, then a separator before the row's own actions; Copy copies the row's texts joined by one
    /// space, and an item copies its value.
    /// </summary>
    [Fact]
    public Task TextOfAndOrder() => ViewSession.RunAsync(async () =>
    {
        Button row = new() { Content = new StackPanel { Children = { new TextBlock { Text = "M" }, new TextBlock { Text = "Notes.md" }, new TextBlock { Text = "docs" } } } };
        Window window = ViewSession.Show(row);
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);
        ContextMenu menu = new() { Items = { new MenuItem { Header = "Open Diff" }, new MenuItem { Header = "Delete" } } };

        RowCopy.Add(menu, row, ("Copy path", () => Task.FromResult<string?>("/repo/docs/Notes.md")), ("Copy nothing", () => Task.FromResult<string?>(null)));

        Assert.Equal(["Copy", "Copy path", "Copy nothing", "-", "Open Diff", "Delete"],
            menu.Items.Select(item => item is MenuItem named ? named.Header as string : "-"));
        Assert.Equal("M Notes.md docs", RowCopy.TextOf(row));
        MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await ViewSession.WaitForAsync(() => copies.Requests.Count == 2);
        Assert.Equal(["M Notes.md docs", "/repo/docs/Notes.md"], copies.Requests);
        Assert.Equal(["Copy"], RowCopy.Menu(row).Items.OfType<MenuItem>().Select(item => item.Header));
    });
}
