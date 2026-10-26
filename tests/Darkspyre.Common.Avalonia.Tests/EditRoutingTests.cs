using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using Darkspyre.Agent.Common.UI.Code;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Dialogs;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class EditRoutingTests
{
    /// <summary>
    /// Copy goes to a focused text box first, then to a focused view that copies its own text, then to the window's
    /// selection. A press on plain text moves focus from a text box to the scope; a press on empty space leaves the text box
    /// focused. Select All with nothing selected reaches the focused scope, and in a freshly opened sheet or confirmation
    /// selects only the dialog's text, also with a selection left behind it. Paste reaches a text box inside a scope.
    /// </summary>
    [Fact]
    public Task FocusedTextBoxThenTargetThenSelection() => ViewSession.RunAsync(async () =>
    {
        TextBox box = new() { Text = "box words", Width = 300 };
        TextBlock plain = new() { Text = "Window text" };
        Panel empty = new() { Height = 40, Background = Brushes.Transparent };
        SourceView source = new() { Text = "source line\n", Height = 80 };
        TextSelectionScope scope = new() { Child = new StackPanel { Children = { box, plain, empty, source } } };
        ModalLayer layer = new();
        Window window = ViewSession.Show(new Grid { Children = { scope, layer } });
        layer.Attach(window);
        CopyHandlerFixture.Attach(window);

        Input.Drag(window, Input.At(plain, window, 1), Input.At(plain, window, plain.Bounds.Width - 1));
        Assert.Equal("Window text", scope.SelectedText);
        box.Focus();
        box.SelectAll();
        Assert.True(EditRouting.Run(window, EditCommand.Copy));
        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == "box words");

        TextEditor editor = Assert.Single(ViewSession.All<TextEditor>(source));
        editor.TextArea.Focus();
        editor.Select(0, "source".Length);
        Assert.True(EditRouting.Run(window, EditCommand.Copy));
        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == "source");

        window.FocusManager!.Focus(null);
        Assert.True(EditRouting.Run(window, EditCommand.Copy));
        await ViewSession.WaitForAsync(() => CopyHandlerFixture.ClipboardAsync(window).Result == "Window text");

        box.Focus();
        Input.Click(window, Input.At(empty, window, 10));
        Assert.Same(box, window.FocusManager!.GetFocusedElement());
        Input.Click(window, Input.At(plain, window, 2));
        Assert.Same(scope, window.FocusManager.GetFocusedElement());
        Assert.False(scope.CanCopy);
        Input.Press(window, Input.Keys.SelectAll[0]);
        Assert.Equal("Window text", scope.SelectedText);

        Sheet sheet = new(layer, "Sheet title", new TextBlock { Text = "Sheet body" }, description: "Sheet description");
        sheet.Show();
        ViewSession.Settle();
        Input.Press(window, Input.Keys.SelectAll[0]);
        TextSelectionScope sheetScope = Assert.IsType<TextSelectionScope>(sheet.View.Parent);
        Assert.Equal("Sheet title\nSheet description\nSheet body", sheetScope.SelectedText);
        Assert.False(scope.CanCopy);
        sheet.Close();
        ViewSession.Settle();

        scope.SelectAll();
        Task<(bool, bool)> asked = ConfirmDialog.AskAsync(layer, "Confirm title", "Confirm message");
        ViewSession.Settle();
        Input.Press(window, Input.Keys.SelectAll[0]);
        TextSelectionScope dialogScope = Assert.Single(ViewSession.All<TextSelectionScope>(layer));
        Assert.Equal("Confirm title\nConfirm message", dialogScope.SelectedText);
        Assert.False(scope.CanCopy);
        Assert.Same(dialogScope, TextSelection.CurrentScope(window));
        Input.Press(window, new global::Avalonia.Input.KeyGesture(global::Avalonia.Input.Key.Escape));
        Assert.Equal((false, false), await asked);

        await TextCopy.WriteAsync(window, " pasted");
        box.Focus();
        box.CaretIndex = box.Text!.Length;
        box.SelectionStart = box.SelectionEnd = box.CaretIndex;
        Assert.True(EditRouting.Run(window, EditCommand.Paste));
        await ViewSession.WaitForAsync(() => box.Text == "box words pasted");
    });
}
