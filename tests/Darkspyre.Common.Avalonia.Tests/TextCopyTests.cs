using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Controls;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class TextCopyTests
{
    /// <summary>
    /// A copy request from a control deep in a window reaches the window's handler and completes with its result; a
    /// window with no handler completes it false and writes nothing.
    /// </summary>
    [Fact]
    public Task ARequestReachesTheWindowHandlerAndCompletes() => ViewSession.RunAsync(async () =>
    {
        TextBlock nested = new() { Text = "nested" };
        Window window = ViewSession.Show(new Border { Child = new StackPanel { Children = { nested } } });
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);

        Assert.True(await TextCopy.RequestAsync(nested, "first"));
        Assert.Equal("first", await CopyHandlerFixture.ClipboardAsync(window));
        copies.Refuse = true;
        Assert.False(await TextCopy.RequestAsync(nested, "second"));
        Assert.Equal(["first", "second"], copies.Requests);

        TextBlock alone = new() { Text = "alone" };
        Window bare = ViewSession.Show(alone);
        Assert.False(await TextCopy.RequestAsync(alone, "third"));
        Assert.NotEqual("third", await CopyHandlerFixture.ClipboardAsync(bare));
    });
}
