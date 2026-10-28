using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>A tool call's lifecycle state on its card.</summary>
public enum ToolCallStatus
{
    /// <summary>The call runs, or waits for an answer.</summary>
    Running,

    /// <summary>The call finished.</summary>
    Success,

    /// <summary>The call failed.</summary>
    Error,
}

/// <summary>What a tool call's card shows.</summary>
/// <param name="Title">The name shown in the summary row.</param>
/// <param name="Status">The call's state, which colours the dot and names the chip.</param>
/// <param name="Input">The call's input, or null.</param>
/// <param name="Output">The call's output, shown unless the call failed with an error message.</param>
/// <param name="Error">The error shown instead of the output when the call failed.</param>
public sealed record ToolCallItem(string Title, ToolCallStatus Status, string? Input = null, string? Output = null, string? Error = null);

/// <summary>
/// One tool invocation: a summary row with a status dot, the tool's name in the code font, with the files it names as
/// links, and a status chip; expanded, its input and its output or error, each with a copy button. The card is kept while its call changes, so whether it
/// is open stays.
/// </summary>
public sealed class ToolCallCard : UserControl
{
    private static readonly string[] States = ["running", "error", "success"];
    private readonly Expander expander;
    private readonly FileLinkText name;
    private readonly TextBlock chip = new();
    private readonly StackPanel body = new() { Spacing = 8 };
    // The input, error and output blocks, in that order; each is made the first time it has text and leaves the body while it has none.
    private readonly (StackPanel Block, TextBlock Text)?[] blocks = new (StackPanel, TextBlock)?[3];
    private ToolCallItem? shown;

    /// <summary>Creates the card showing <paramref name="item"/>.</summary>
    /// <param name="item">What the card shows.</param>
    /// <param name="context">The transcript's context: it finds the files the title names and opens them.</param>
    public ToolCallCard(ToolCallItem item, TranscriptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.name = new(context.Links, context.OpenLink) { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Border dot = new() { Width = 8, Height = 8, CornerRadius = new Avalonia.CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        dot.Classes.Add("tool-call-dot");
        this.name.Classes.Add("tool-call-name");
        Border status = new() { Child = this.chip, VerticalAlignment = VerticalAlignment.Center };
        status.Classes.Add("tool-call-chip");
        DockPanel header = new();
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(dot);
        header.Children.Add(status);
        header.Children.Add(this.name);
        this.expander = new() { Header = header, Content = this.body, HorizontalAlignment = HorizontalAlignment.Stretch };
        this.expander.Classes.Add("tool-call-card");
        // The title joins a drag only while the card is open; a press on it still opens or closes the card. Closed, the
        // card's menu copies its title.
        TranscriptMarks.ToolRow(this.name, header, this.expander);
        this.Content = this.expander;
        this.Show(item);
    }

    /// <summary>Changes the card to show <paramref name="item"/>, in place: the card and whether it is open stay.</summary>
    public void Show(ToolCallItem item)
    {
        if (item == this.shown) return;
        this.shown = item;
        string state = item.Status switch
        {
            ToolCallStatus.Running => "running",
            ToolCallStatus.Error => "error",
            _ => "success",
        };
        foreach (string each in States) this.expander.Classes.Set(each, each == state);
        this.name.Value = item.Title;
        ToolTip.SetTip(this.name, item.Title);
        this.chip.Text = item.Status switch { ToolCallStatus.Running => "Running", ToolCallStatus.Error => "Error", _ => "Done" };
        bool failed = item.Status == ToolCallStatus.Error && item.Error is { Length: > 0 };
        this.Set(0, "Input", "tool-call-input", item.Input);
        this.Set(1, "Error", "tool-call-error", failed ? item.Error : null);
        this.Set(2, "Output", "tool-call-output", failed ? null : item.Output);
    }

    private void Set(int index, string title, string kind, string? text)
    {
        if (text is not { Length: > 0 })
        {
            if (this.blocks[index] is { } gone) this.body.Children.Remove(gone.Block);
            return;
        }
        (StackPanel Block, TextBlock Text) block = this.blocks[index] ??= Block(title, kind);
        if (block.Text.Text != text) block.Text.Text = text;
        if (this.body.Children.Contains(block.Block)) return;
        int at = this.blocks.Take(index).Count(earlier => earlier is { } shownBefore && this.body.Children.Contains(shownBefore.Block));
        this.body.Children.Insert(at, block.Block);
    }

    private static (StackPanel Block, TextBlock Text) Block(string title, string kind)
    {
        TextBlock content = new() { TextWrapping = TextWrapping.Wrap };
        content.Classes.Add("tool-call-text");
        ConversationText.In(content);
        Button copy = null!;
        copy = Forms.Button("Copy", "ghost", () => CopyFeedback.CopyAsync(copy, content.Text ?? string.Empty), ButtonSize.ExtraSmall);
        Avalonia.Automation.AutomationProperties.SetName(copy, $"Copy {title.ToLowerInvariant()}");
        DockPanel heading = new();
        DockPanel.SetDock(copy, Dock.Right);
        heading.Children.Add(copy);
        heading.Children.Add(Forms.Text(title, "tool-call-label"));
        Border box = new() { Child = content };
        box.Classes.Add("tool-call-block");
        StackPanel block = new() { Spacing = 4 };
        block.Classes.Add(kind);
        TextSelection.SetIsBlock(block, true);
        block.Children.Add(heading);
        block.Children.Add(box);
        return (block, content);
    }
}
