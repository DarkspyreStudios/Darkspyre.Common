using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Icons;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class CopyFeedbackTests
{
    /// <summary>
    /// After a successful copy a button shows the check and the name "Copied" for two seconds, a second press restarts the
    /// two seconds, and a refused copy shows no check.
    /// </summary>
    [Fact]
    public Task ACheckShowsOnlyWhenTheCopySucceeds() => ViewSession.RunAsync(async () =>
    {
        Icon glyph = new() { Source = AppIcons.Copy };
        Button icon = new() { Content = glyph };
        Button text = new() { Content = "Copy text" };
        Window window = ViewSession.Show(new StackPanel { Children = { icon, text } });
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);

        copies.Refuse = true;
        await CopyFeedback.CopyAsync(icon, "refused");
        Assert.Same(AppIcons.Copy, glyph.Source);
        Assert.Null(AutomationProperties.GetName(icon));

        copies.Refuse = false;
        Stopwatch shown = Stopwatch.StartNew();
        await CopyFeedback.CopyAsync(icon, "copied");
        await CopyFeedback.CopyAsync(text, "copied");
        Assert.Same(AppIcons.Check, glyph.Source);
        Assert.Equal("Copied", AutomationProperties.GetName(icon));
        Assert.Equal("Copied", text.Content);
        Assert.Equal("copied", await CopyHandlerFixture.ClipboardAsync(window));

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await CopyFeedback.CopyAsync(icon, "again");
        Stopwatch restarted = Stopwatch.StartNew();
        await ViewSession.WaitForAsync(() => text.Content is "Copy text");
        Assert.Same(AppIcons.Check, glyph.Source);
        await ViewSession.WaitForAsync(() => ReferenceEquals(glyph.Source, AppIcons.Copy));
        Assert.True(shown.Elapsed >= TimeSpan.FromSeconds(1.9), $"The text check went after {shown.Elapsed}.");
        Assert.True(restarted.Elapsed >= TimeSpan.FromSeconds(1.9), $"The restarted check went after {restarted.Elapsed}.");
        Assert.Null(AutomationProperties.GetName(icon));
    });
}
