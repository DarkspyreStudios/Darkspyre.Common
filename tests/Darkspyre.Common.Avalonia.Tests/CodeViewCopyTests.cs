using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using Darkspyre.Agent.Common.UI.Code;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class CodeViewCopyTests
{
    /// <summary>
    /// In the diff and source views, the Copy key copies the selected lines without line numbers or plus and minus marks,
    /// a right-click opens the selection menu, and Select All then Copy copies the whole document.
    /// </summary>
    [Fact]
    public Task DiffAndSourceCopyLinesWithoutNumbers() => ViewSession.RunAsync(async () =>
    {
        DiffView diff = new() { OldText = "one\ntwo\nthree\n", NewText = "one\n2\nthree\n" };
        SourceView source = new() { Text = "first line\nsecond line\nthird line\n" };
        Grid views = new() { RowDefinitions = new("*,*"), Children = { diff, source } };
        Grid.SetRow(source, 1);
        Window window = ViewSession.Show(new TextSelectionScope { Child = views });
        CopyHandlerFixture.Attach(window);
        await ViewSession.WaitForAsync(() => diff.Hunks.Count == 1);

        foreach ((Control view, string lines) in new (Control, string)[] { (diff, "two\n2"), (source, "second line\nthird line") })
        {
            TextEditor editor = Assert.Single(ViewSession.All<TextEditor>(view));
            int start = editor.Document.Text.IndexOf(lines, StringComparison.Ordinal);
            editor.TextArea.Focus();
            editor.Select(start, lines.Length);
            Input.Press(window, Input.Keys.Copy[0]);
            await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == lines);

            window.MouseDown(Input.At(editor, window, 60), MouseButton.Right);
            window.MouseUp(Input.At(editor, window, 60), MouseButton.Right);
            ViewSession.Settle();
            Assert.True(SelectionMenu.Text.IsOpen);
            SelectionMenu.Text.Hide();

            editor.TextArea.Focus();
            Input.Press(window, Input.Keys.SelectAll[0]);
            Input.Press(window, Input.Keys.Copy[0]);
            await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == editor.Document.Text);
        }
    });
}
