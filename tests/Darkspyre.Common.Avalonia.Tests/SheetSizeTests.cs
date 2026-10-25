using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Dialogs;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class SheetSizeTests
{
    /// <summary>
    /// A sheet with one short line opens at its size's exact width, Medium 720 and Wide 960, and a window narrower than
    /// that keeps it 48 px inside each edge.
    /// </summary>
    [Theory]
    [InlineData(DialogSize.Medium, 720)]
    [InlineData(DialogSize.Wide, 960)]
    public Task ASheetOpensAtItsSizeAndAWindowNarrowerThanThatClampsIt(DialogSize size, double width) => ViewSession.RunAsync(() =>
    {
        Assert.Equal(width, OpenedWidth(size, windowWidth: 1400));
        Assert.Equal(600 - 2 * 48, OpenedWidth(size, windowWidth: 600));
        return Task.CompletedTask;
    });

    private static double OpenedWidth(DialogSize size, double windowWidth)
    {
        ModalLayer layer = new();
        Window window = ViewSession.Show(layer, width: windowWidth, height: 700);
        Sheet sheet = new(layer, "Short", new TextBlock { Text = "One line." }, size: size);
        sheet.Show();
        ViewSession.Settle();
        double opened = sheet.View.Bounds.Width;
        window.Close();
        return opened;
    }
}
