using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace Darkspyre.Common.Avalonia.Markdown;

/// <summary>The code-block copy button's confirmation: the check glyph and the name "Copied" for two seconds, then the
/// copy glyph and the name "Copy" again. A second press restarts the two seconds.</summary>
internal static class CopyFeedback
{
    /// <summary>How long the check shows.</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);

    // A check in the same 24-unit box as LiveMarkdown's copy glyph, so the button's Viewbox scales both alike.
    private static readonly Geometry Check = Geometry.Parse("F1 M24,24z M0,0z M20,6L9,17L4,12");

    private static readonly ConditionalWeakTable<Button, Pending> Shown = [];

    /// <summary>Shows the check on <paramref name="copy"/>, LiveMarkdown's PART_CopyButton.</summary>
    internal static void Show(Button copy)
    {
        Path? glyph = (copy.Content as Viewbox)?.Child as Path;
        Pending pending = Shown.GetValue(copy, _ => new Pending());
        pending.Restore?.Dispose();
        glyph?.SetValue(Path.DataProperty, Check);
        AutomationProperties.SetName(copy, "Copied");
        pending.Restore = DispatcherTimer.RunOnce(() =>
        {
            // Clearing the value brings back the template's copy glyph.
            glyph?.ClearValue(Path.DataProperty);
            AutomationProperties.SetName(copy, "Copy");
            Shown.Remove(copy);
        }, Duration);
    }

    private sealed class Pending
    {
        public IDisposable? Restore { get; set; }
    }
}
