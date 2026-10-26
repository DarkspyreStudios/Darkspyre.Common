using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Dialogs;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class ModalLayerSelectionTests
{
    /// <summary>
    /// Opening a dialog clears the window's selection, so no highlight of the text behind it remains to draw over the
    /// dialog: the window's adorner layer sits above the modal layer.
    /// </summary>
    [Fact]
    public Task OpeningADialogClearsTheSelectionBehindIt() => ViewSession.RunAsync(() =>
    {
        TextBlock text = new() { Text = "Selected words behind the dialog", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top };
        TextSelectionScope scope = new() { Child = text };
        ModalLayer layer = new();
        Window window = ViewSession.Show(new Grid { Children = { scope, layer } });
        Input.Drag(window, Input.At(text, window, 1), Input.At(text, window, 120));
        Assert.True(scope.CanCopy);
        Assert.NotNull(AdornerLayer.GetAdorner(text));

        new Sheet(layer, "A sheet", new TextBlock { Text = "Body" }).Show();
        ViewSession.Settle();

        Assert.Null(TextSelection.CurrentScope(window));
        Assert.False(scope.CanCopy);
        Assert.Null(AdornerLayer.GetAdorner(text));
        return Task.CompletedTask;
    });
}
