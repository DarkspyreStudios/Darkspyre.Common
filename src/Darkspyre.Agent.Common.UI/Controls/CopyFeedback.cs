using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Darkspyre.Agent.Common.UI.Icons;
using Path = Avalonia.Controls.Shapes.Path;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>A copy button's confirmation: a check in place of its icon or label for two seconds, then the icon or label
/// again. A second call restarts the two seconds.</summary>
public static class CopyFeedback
{
    // A check in the same 24-unit box as LiveMarkdown's copy glyph, so the code-block button's Viewbox scales both alike.
    private static readonly Geometry CodeBlockCheck = Geometry.Parse("F1 M24,24z M0,0z M20,6L9,17L4,12");

    private static readonly ConditionalWeakTable<Button, Pending> Shown = [];

    /// <summary>How long the check shows.</summary>
    public static TimeSpan Duration { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Shows the check on <paramref name="button"/>: an icon button's <c>Icon</c> content swaps to
    /// <c>AppIcons.Check</c>, a text button's label to "Copied", LiveMarkdown's code-block button's glyph to the check path.
    /// Its automation name is "Copied" while the check shows.</summary>
    /// <param name="button">The copy button that was pressed.</param>
    public static void Show(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!Shown.TryGetValue(button, out Pending? pending))
        {
            pending = new Pending(button);
            Shown.Add(button, pending);
            pending.Swap();
        }
        pending.Restore?.Dispose();
        pending.Restore = DispatcherTimer.RunOnce(() =>
        {
            pending.Undo();
            Shown.Remove(button);
        }, Duration);
    }

    /// <summary>Requests the copy of <paramref name="text"/> from <paramref name="button"/> and shows the check only when
    /// the copy succeeds.</summary>
    /// <param name="button">The copy button that was pressed; the request is raised from it.</param>
    /// <param name="text">The exact text to copy.</param>
    public static async Task CopyAsync(Button button, string text)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (await TextCopy.RequestAsync(button, text)) Show(button);
    }

    // What the button showed before the check, put back when the check goes.
    private sealed class Pending(Button button)
    {
        private readonly string? name = AutomationProperties.GetName(button);
        private Icon? icon;
        private IconSource? iconSource;
        private string? label;
        private Path? glyph;

        public IDisposable? Restore { get; set; }

        public void Swap()
        {
            switch (button.Content)
            {
                case Icon shown:
                    this.icon = shown;
                    this.iconSource = shown.Source;
                    shown.Source = AppIcons.Check;
                    break;
                case string text:
                    this.label = text;
                    button.Content = "Copied";
                    break;
                case Viewbox { Child: Path path }:
                    this.glyph = path;
                    path.SetValue(Path.DataProperty, CodeBlockCheck);
                    break;
            }
            AutomationProperties.SetName(button, "Copied");
        }

        public void Undo()
        {
            if (this.icon is not null) this.icon.Source = this.iconSource;
            if (this.label is not null) button.Content = this.label;
            if (this.glyph is not null)
            {
                // Clearing the value brings back the template's copy glyph, and the button is named for it again.
                this.glyph.ClearValue(Path.DataProperty);
                AutomationProperties.SetName(button, "Copy");
                return;
            }
            if (this.name is null) button.ClearValue(AutomationProperties.NameProperty);
            else AutomationProperties.SetName(button, this.name);
        }
    }
}
