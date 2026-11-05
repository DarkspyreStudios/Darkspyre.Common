using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Markdown;
using LiveMarkdown.Avalonia;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

/// <summary>Wide tables keep every cell's text inside the shared viewer.</summary>
public sealed class FittedTableTests
{
    /// <summary>A four-column table and an over-long code word wrap within a 300-pixel viewer.</summary>
    [Fact]
    public Task WholeTableAndEveryCellFit() => ViewSession.RunAsync(async () =>
    {
        string word = new('W', 80);
        MarkdownViewer viewer = new()
        {
            Text = $"| First | Second | Third | Long text |\n| --- | --- | --- | --- |\n"
                + $"| One | Two | Three | **Last** [link](https://example.com) with several sentences that stay readable. `{word}` END |",
        };
        Window window = ViewSession.Show(viewer, 300, 700);
        try
        {
            await ViewSession.WaitForAsync(() => ViewSession.All<MarkdownTextBlock>(viewer)
                .Any(text => text.ActualText.EndsWith("END", StringComparison.Ordinal)));
            Grid table = Assert.Single(ViewSession.All<Grid>(viewer), grid => grid.Classes.Contains("fitted-table"));
            Assert.True(table.DesiredSize.Width <= 300.1, $"Table desired width: {table.DesiredSize.Width}");
            Assert.True(table.Bounds.Width <= 300.1, $"Table arranged width: {table.Bounds.Width}");
            Border[] cells = [.. table.Children.OfType<Border>()];
            Assert.Equal(8, cells.Length);
            foreach (Border cell in cells)
            {
                MarkdownTextBlock text = Assert.IsType<MarkdownTextBlock>(cell.Child);
                Assert.NotEmpty(text.TextLayout.TextLines);
                var last = text.TextLayout.TextLines[^1];
                Assert.True(last.FirstTextSourceIndex + last.Length >= text.LayoutText.Length, text.ActualText);
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + 0.1,
                    $"Cell layout {text.TextLayout.Width} exceeds {text.Bounds.Width}: {text.ActualText}");
                Assert.True(text.TextLayout.Height <= text.Bounds.Height + 0.1, text.ActualText);
            }
            MarkdownTextBlock final = Assert.IsType<MarkdownTextBlock>(cells[^1].Child);
            Assert.Equal($"Last link with several sentences that stay readable. {word} END", final.ActualText);
            Assert.True(final.TextLayout.TextLines.Count > 1);
        }
        finally { window.Close(); }
    });
}
