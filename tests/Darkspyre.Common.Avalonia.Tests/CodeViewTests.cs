using Darkspyre.Agent.Common.UI.Code;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class CodeViewTests
{
    [Fact]
    public Task DiffViewShowsEachHunkAndItsNoFinalNewlineNote() => ViewSession.RunAsync(async () =>
    {
        DiffView view = new() { OldText = "one\ntwo\n", NewText = "one\n2" };
        ViewSession.Show(view);

        await ViewSession.WaitForAsync(() => view.Hunks.Count == 1);
        string shown = Assert.Single(ViewSession.All<TextEditor>(view)).Document.Text;
        Assert.Equal("@@ -1,2 +1,2 @@\none\ntwo\n2\nNo final newline", shown);
    });

    [Fact]
    public Task SourceViewFindsEveryMatchAndWrapsAround() => ViewSession.RunAsync(() =>
    {
        SourceView view = new() { Text = "alpha\nbeta ALPHA\ngamma\nalpha", Language = ".cs" };
        ViewSession.Show(view);

        Assert.Equal(3, view.Find("alpha"));
        Assert.Equal(1, view.CurrentMatch);
        view.FindPrevious();
        Assert.Equal(3, view.CurrentMatch);
        view.FindNext();
        Assert.Equal(1, view.CurrentMatch);
        Assert.Equal(0, view.Find("missing"));
        return Task.CompletedTask;
    });

    /// <summary>The Source view draws each line of the file, coloured by its language, here an HTML page.</summary>
    [Fact]
    public Task SourceViewDrawsTheFileHighlighted() => ViewSession.RunAsync(async () =>
    {
        SourceView view = new() { Text = "<p class=\"note\">Hi</p>\n<script>const x = 1;</script>\n", Language = ".html" };
        ViewSession.Show(view);

        TextView text = Assert.Single(ViewSession.All<TextEditor>(view)).TextArea.TextView;
        await ViewSession.WaitForAsync(() => text.VisualLinesValid && text.VisualLines.Count == 3
            && text.VisualLines[0].Elements.Select(element => (element.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color).Distinct().Count() > 1);
    });

    [Fact]
    public Task SourceViewGoesToItsTargetLine() => ViewSession.RunAsync(() =>
    {
        SourceView view = new() { Text = string.Join("\n", Enumerable.Range(1, 200).Select(number => $"line {number}")) };
        ViewSession.Show(view);

        view.GoToLine(150);
        ViewSession.Settle();

        Assert.Equal(150, Assert.Single(ViewSession.All<TextEditor>(view)).TextArea.Caret.Line);
        return Task.CompletedTask;
    });
}
