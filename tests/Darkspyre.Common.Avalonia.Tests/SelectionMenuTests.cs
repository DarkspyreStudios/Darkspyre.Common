using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Markdown;
using LiveMarkdown.Avalonia;
using Xunit;
using TextHighlight = Darkspyre.Agent.Common.UI.Controls.TextHighlight;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class SelectionMenuTests
{
    private static readonly bool[] HiddenThenRevealed = [false, true];

    /// <summary>
    /// A right-click on plain text and on a reply's text opens the one selection menu, Copy and Select All with their
    /// keys; Copy is off without a selection and on with one; opening the menu keeps the selection; plain text and Markdown
    /// text are highlighted with the same brush.
    /// </summary>
    [Fact]
    public Task ARightClickOnAnyTextOpensTheOneMenu() => ViewSession.RunAsync(async () =>
    {
        TextBlock plain = new() { Text = "Plain text here" };
        MarkdownViewer reply = new() { Text = "Reply text here" };
        TextSelectionScope scope = new() { Child = new StackPanel { Children = { plain, reply } } };
        Window window = ViewSession.Show(scope);
        SolidColorBrush expected = new(Colors.DodgerBlue, 0.4);
        window.Resources["color-primary.brush.40"] = expected;
        await ViewSession.WaitForAsync(() => window.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(block => block.Inlines?.Text is { Length: > 0 }));
        MarkdownTextBlock markdown = window.GetVisualDescendants().OfType<MarkdownTextBlock>().First(block => block.Inlines?.Text is { Length: > 0 });
        MenuFlyout menu = SelectionMenu.Text;
        MenuItem[] items = [.. menu.Items.Cast<MenuItem>()];
        Assert.Equal(["Copy", "Select All"], items.Select(item => item.Header));

        foreach (Visual text in new Visual[] { plain, markdown })
        {
            RightClick(window, Input.At(text, window, 3));
            Assert.True(menu.IsOpen);
            Assert.False(items[0].IsEnabled);
            Assert.Equal(Input.Keys.Copy[0], items[0].InputGesture);
            Assert.Equal(Input.Keys.SelectAll[0], items[1].InputGesture);
            menu.Hide();
        }

        Input.Drag(window, Input.At(plain, window, 1), Input.At(markdown, window, 30));
        string selected = scope.SelectedText;
        Assert.StartsWith("Plain text here\nRe", selected, StringComparison.Ordinal);
        RightClick(window, Input.At(markdown, window, 3));
        Assert.True(menu.IsOpen);
        Assert.True(items[0].IsEnabled);
        Assert.Equal(selected, scope.SelectedText);
        menu.Hide();

        Assert.Same(expected, markdown.SelectionBrush);
        Assert.Same(expected, Assert.IsType<TextHighlight>(AdornerLayer.GetAdorner(plain)).Brush);
    });

    /// <summary>
    /// A text box's menu holds Cut, Copy, Paste and Select All. In a secret box, hidden and then revealed, Cut and Copy
    /// stay off and the Copy key copies nothing.
    /// </summary>
    [Fact]
    public Task TextBoxMenuAndSecretBox() => ViewSession.RunAsync(async () =>
    {
        TextBox box = new() { Text = "plain words", Width = 300 };
        TextBox secret = new() { Text = "hunter2", PasswordChar = '•', Width = 300 };
        Window window = ViewSession.Show(new TextSelectionScope { Child = new StackPanel { Children = { box, secret } } });
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);
        await TextCopy.WriteAsync(window, "before");
        MenuFlyout menu = SelectionMenu.TextBox;
        MenuItem[] items = [.. menu.Items.Cast<MenuItem>()];
        Assert.Equal(["Cut", "Copy", "Paste", "Select All"], items.Select(item => item.Header));

        box.Focus();
        box.SelectAll();
        RightClick(window, Input.At(box, window, 20));
        Assert.True(menu.IsOpen);
        Assert.True(items[0].IsEnabled && items[1].IsEnabled);
        Assert.Equal(Input.Keys.Cut[0], items[0].InputGesture);
        menu.Hide();

        foreach (bool revealed in HiddenThenRevealed)
        {
            secret.RevealPassword = revealed;
            secret.Focus();
            secret.SelectAll();
            RightClick(window, Input.At(secret, window, 20));
            Assert.True(menu.IsOpen);
            Assert.False(items[0].IsEnabled);
            Assert.False(items[1].IsEnabled);
            Assert.True(items[2].IsEnabled);
            menu.Hide();
            secret.Focus();
            secret.SelectAll();
            Input.Press(window, Input.Keys.Copy[0]);
        }
        Assert.Empty(copies.Requests);
        Assert.Equal("before", await CopyHandlerFixture.ClipboardAsync(window));
    });

    private static void RightClick(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        ViewSession.Settle();
    }
}
