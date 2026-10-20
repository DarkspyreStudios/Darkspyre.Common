using Darkspyre.Agent.Common.UI.Code;
using Avalonia.Controls;
using AvaloniaEdit;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class CodeViewTests
{
    [Fact]
    public void AChangedLineEndingIsAChangedLine()
    {
        IReadOnlyList<DiffHunk> hunks = TextDiff.Compute(TextLine.Split("a\nb\nc\n"), TextLine.Split("a\r\nb\nc\n"));

        DiffHunk hunk = Assert.Single(hunks);
        Assert.Equal((1, 1), (hunk.OldStartLine, hunk.NewStartLine));
        Assert.Equal([DiffLineKind.Remove, DiffLineKind.Add, DiffLineKind.Context, DiffLineKind.Context], hunk.Lines.Select(line => line.Kind));
    }

    [Fact]
    public void FarApartChangesAreSeparateHunks()
    {
        string before = string.Join("\n", Enumerable.Range(1, 30).Select(number => $"line {number}"));
        string after = before.Replace("line 2\n", "line two\n", StringComparison.Ordinal).Replace("line 28", "line twenty-eight", StringComparison.Ordinal);

        IReadOnlyList<DiffHunk> hunks = TextDiff.Compute(TextLine.Split(before), TextLine.Split(after));

        Assert.Equal(2, hunks.Count);
        Assert.Equal(25, hunks[1].OldStartLine);
    }

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
    public Task DiffViewOfEqualTextsSaysSo() => ViewSession.RunAsync(async () =>
    {
        DiffView view = new() { OldText = "same\n", NewText = "same\n" };
        ViewSession.Show(view);

        await ViewSession.WaitForAsync(() => ViewSession.All<TextBlock>(view).Any(block => block.IsEffectivelyVisible && block.Text == "No text differences."));
    });

    /// <summary>Side by side pairs a removed and an added run row for row with a filler, and an absent side differs from an empty file.</summary>
    [Fact]
    public Task SideBySideAlignsChangesAndShowsAbsentSide() => ViewSession.RunAsync(async () =>
    {
        DiffView view = new()
        {
            ViewMode = DiffViewMode.SideBySide, OldLabel = "HEAD", NewLabel = "Working tree",
            OldText = "a\nb\nc\n", NewText = "a\nB\nB2\nc\n",
        };
        ViewSession.Show(view);

        await ViewSession.WaitForAsync(() => view.Hunks.Count == 1);
        TextEditor[] columns = [.. ViewSession.All<TextEditor>(view)];
        Assert.Equal(2, columns.Length);
        Assert.Equal("@@ -1,3 +1,4 @@\na\nb\n\nc", columns[0].Document.Text);
        Assert.Equal("@@ -1,3 +1,4 @@\na\nB\nB2\nc", columns[1].Document.Text);
        Assert.Contains(ViewSession.All<TextBlock>(view), block => block.IsEffectivelyVisible && block.Text == "HEAD");
        Assert.Contains(ViewSession.All<TextBlock>(view), block => block.IsEffectivelyVisible && block.Text == "Working tree");

        view.OldText = null;
        view.NewText = "x\n";
        await ViewSession.WaitForAsync(() => columns[1].Document.Text == "@@ -1,0 +1,1 @@\nx");
        Assert.Contains(ViewSession.All<TextBlock>(view), block => block.IsEffectivelyVisible && block.Text == DiffView.AbsentMessage);

        view.OldText = string.Empty;
        await ViewSession.WaitForAsync(() => !ViewSession.All<TextBlock>(view).Any(block => block.IsEffectivelyVisible && block.Text == DiffView.AbsentMessage));
        Assert.True(columns[0].IsEffectivelyVisible);
    });

    [Fact]
    public Task SideBySideFindCountsContextOnce() => ViewSession.RunAsync(async () =>
    {
        DiffView view = new() { ViewMode = DiffViewMode.SideBySide, OldText = "same x\nold x\n", NewText = "same x\nnew x\n" };
        ViewSession.Show(view);
        await ViewSession.WaitForAsync(() => view.Hunks.Count == 1);

        Assert.Equal(3, view.Find("x"));
        Assert.Equal(1, view.CurrentMatch);
        view.FindNext();
        view.FindNext();
        Assert.Equal(3, view.CurrentMatch);
        view.FindNext();
        Assert.Equal(1, view.CurrentMatch);
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

    /// <summary>A new text, such as a refreshed file, keeps the place the person scrolled to instead of returning to the target line.</summary>
    [Fact]
    public Task SourceViewKeepsItsScrollPositionWhenTheTextChanges() => ViewSession.RunAsync(() =>
    {
        SourceView view = new() { Text = string.Join("\n", Enumerable.Range(1, 200).Select(number => $"line {number}")) };
        ViewSession.Show(view);
        view.GoToLine(150);
        TextEditor editor = Assert.Single(ViewSession.All<TextEditor>(view));
        editor.ScrollToLine(40);
        ViewSession.Settle();
        double offset = editor.VerticalOffset;

        view.Text = string.Join("\n", Enumerable.Range(1, 200).Select(number => $"line {number} changed"));
        ViewSession.Settle();

        Assert.Equal(offset, editor.VerticalOffset);
        return Task.CompletedTask;
    });
}
