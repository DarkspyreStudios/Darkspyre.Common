using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>
/// One stretch of work between replies: reasoning, tool calls and their output, and context compactions. Collapsed, it
/// is one faint line naming the latest step, which shimmers while the turn is still working on it. While the model
/// writes a tool call, that line names the tool; while the runtime reports its own progress, it is the latest
/// progress. A running call that reports progress shows it on its card and on that line. The row is kept across
/// redraws, so whether it is open stays.
/// </summary>
public sealed class ToolActivity : UserControl
{
    /// <summary>The collapsed line and card title of a context compaction.</summary>
    internal const string CompactionTitle = "Context compacted";

    /// <summary>The card title of a failure notice.</summary>
    internal const string NoticeTitle = "Failure";

    /// <summary>The collapsed line and card title of a background worker's response.</summary>
    internal const string WorkerTitle = "Background worker response";

    /// <summary>The collapsed line and card title of a turn the application sent to the model.</summary>
    internal const string ApplicationTitle = "Application message";

    private readonly TranscriptContext context;
    private readonly Expander expander = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly FileLinkText line;
    private readonly StackPanel blocks = new() { Spacing = 8 };
    private readonly Border warning = new() { Child = new Icon { Source = AppIcons.Warning, Width = 12, Height = 12 } };
    private readonly KeyedViews<string, Block, Control> views;
    private IReadOnlyList<TranscriptMessage> messages = [];

    /// <summary>Creates the row.</summary>
    public ToolActivity(TranscriptContext context)
    {
        this.context = context;
        this.line = new(context.Links, context.OpenLink) { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        this.views = new(block => block.Key, this.Create, Update);
        this.line.Classes.Add("activity-line");
        this.warning.Classes.Add("tool-activity-warning");
        Avalonia.Automation.AutomationProperties.SetName(this.warning, "A failure was recorded");
        StackPanel summary = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        summary.Children.Add(this.warning);
        summary.Children.Add(this.line);
        this.expander.Header = summary;
        this.expander.Content = this.blocks;
        this.expander.Classes.Add("tool-activity");
        this.blocks.Classes.Add("tool-activity-blocks");
        // The collapsed line joins a drag only while the stretch is open; a collapsed stretch is skipped, and its menu
        // copies the line.
        TranscriptMarks.ToolRow(this.line, summary, this.expander);
        // Expanding bubbles, so a card opening inside the row raises it here too; only the row's own opening draws the blocks.
        this.expander.Expanding += (_, args) =>
        {
            if (ReferenceEquals(args.Source, this.expander)) this.RenderBlocks();
        };
        this.Content = this.expander;
    }

    /// <summary>Redraws the row for <paramref name="current"/>: the stretch's messages, whether the turn still works on it, and the runtime's latest progress.</summary>
    public void Update(IReadOnlyList<TranscriptMessage> current, bool live, string progress)
    {
        this.messages = current;
        this.warning.IsVisible = current.Any(message => message.Role == TranscriptMessage.NoticeRole);
        this.line.Value = Line(current, live, progress);
        this.line.Classes.Set("live", live);
        // The blocks are built only while the row is open.
        if (this.expander.IsExpanded) this.RenderBlocks();
    }

    /// <summary>The collapsed line: the latest reasoning line, or the latest tool and what it acts on.</summary>
    internal static string Line(IReadOnlyList<TranscriptMessage> messages, bool live, string progress)
    {
        if (live && progress.Length > 0) return progress;
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            TranscriptMessage message = messages[index];
            if (message.Role == TranscriptMessage.ReasoningRole
                && message.Text.Split('\n').Select(text => text.Trim()).LastOrDefault(text => text.Length > 0) is { } thought)
                return thought.Trim('*', '#', ' ');
            if (message.Call is { } call)
                return RunningProgress(call, ResultFor(messages, call)) is { } running ? $"{call.Label} · {ProgressText(running)}" : call.Label;
            if (message.Role == "tool" && !HasCall(messages, message)) return "Tool";
            if (message.Role == TranscriptMessage.CompactionRole) return CompactionTitle;
            if (message.Role == TranscriptMessage.WorkerRole) return WorkerTitle;
            if (message.Role == TranscriptMessage.NoticeRole)
                return message.Text.Split('\n').Select(text => text.Trim()).FirstOrDefault(text => text.Length > 0) ?? NoticeTitle;
        }
        // An application turn names the line only when the stretch holds nothing else, so the line keeps naming the
        // tool the turn answers.
        if (messages.FirstOrDefault(message => message.Role == TranscriptMessage.ApplicationRole) is { } application)
            return application.ActivityTitle ?? ApplicationTitle;
        return live ? "Working" : "Activity";
    }

    /// <summary>Progress as text: the message, then the value and any total.</summary>
    internal static string ProgressText(TranscriptProgress progress)
    {
        string amount = progress.Total is { } total
            ? $"{progress.Value.ToString("0.##", CultureInfo.CurrentCulture)} of {total.ToString("0.##", CultureInfo.CurrentCulture)}"
            : progress.Value.ToString("0.##", CultureInfo.CurrentCulture);
        return progress.Message is { Length: > 0 } message ? $"{message.ReplaceLineEndings(" ")} ({amount})" : amount;
    }

    /// <summary>The state a call's card shows, including a waiting question.</summary>
    internal static ToolCallStatus StatusFor(TranscriptCall call, TranscriptMessage? result) =>
        call.Running && result is null || OutputForCall(call, result)?.WaitingForAnswer == true ? ToolCallStatus.Running
        : Failed(call, result) ? ToolCallStatus.Error : ToolCallStatus.Success;

    private static TranscriptOutput? OutputForCall(TranscriptCall call, TranscriptMessage? result) =>
        result?.Outputs.FirstOrDefault(output => output.CallId == call.CallId);

    /// <summary>The output a call's card shows.</summary>
    internal static string? OutputFor(TranscriptCall call, TranscriptMessage? result) =>
        OutputForCall(call, result) is { } output ? output.QuestionOutput ?? InspectionText([output], result!.Text) : null;

    /// <summary>The result message of a call, or null.</summary>
    internal static TranscriptMessage? ResultFor(IReadOnlyList<TranscriptMessage> messages, TranscriptCall call) =>
        messages.FirstOrDefault(message => message.Outputs.Any(output => output.CallId == call.CallId));

    internal static bool Failed(TranscriptCall call, TranscriptMessage? result) => OutputForCall(call, result)?.Failed ?? call.Success == false;

    private static TranscriptProgress? RunningProgress(TranscriptCall call, TranscriptMessage? result) => call.Running && result is null ? call.Progress : null;

    private static bool HasCall(IReadOnlyList<TranscriptMessage> messages, TranscriptMessage message) =>
        message.Outputs.Count > 0 && message.Outputs.All(output => messages.Any(other => other.Call?.CallId == output.CallId));

    private static string InspectionText(TranscriptOutput[] outputs, string fallback)
    {
        if (outputs.Length == 0) return fallback;
        string text = string.Join("\n", outputs.SelectMany(output => output.Parts).OfType<TranscriptTextPart>()
            .Where(part => !part.ForPerson).Select(part => part.Text));
        return text.Length > 0 ? text : outputs.All(output => output.Parts.Count == 0) ? "No content returned." : string.Empty;
    }

    private static bool HasRichInspection(TranscriptOutput tool) =>
        tool.Structured is not null || tool.Details is not null || tool.Parts.Any(part => part.OmittedBytes is not null
            || !part.ForPerson && (part is not TranscriptTextPart || part.Metadata is not null));

    // Each block is kept by its message's key while the stretch is open, so a card stays open and a reasoning block keeps
    // its view while the turn works; only a block whose message changed is updated.
    private void RenderBlocks() => this.views.Apply(this.blocks.Children, this.Blocks());

    private List<Block> Blocks()
    {
        List<Block> shown = [];
        HashSet<string> keys = [];
        for (int index = 0; index < this.messages.Count; index++)
        {
            TranscriptMessage message = this.messages[index];
            string key = KeyOf(message, index);
            // A key is unique within the stretch; a repeat takes its position as well.
            if (!keys.Add(key)) keys.Add(key = $"{key}@{index}");
            if (message.Role == TranscriptMessage.ReasoningRole) shown.Add(new(key, message.Text, null, null, null));
            else if (message.Role == TranscriptMessage.CompactionRole) shown.Add(Card(key, new(CompactionTitle, ToolCallStatus.Success, Output: message.Text)));
            else if (message.Role == TranscriptMessage.NoticeRole) shown.Add(Card(key, new(NoticeTitle, ToolCallStatus.Error, Error: message.Text)));
            else if (message.Role == TranscriptMessage.WorkerRole) shown.Add(Card(key, new(WorkerTitle, ToolCallStatus.Success, Output: message.Text)));
            else if (message.Role == TranscriptMessage.ApplicationRole)
                shown.Add(Card(key, new(message.ActivityTitle ?? ApplicationTitle, ToolCallStatus.Success, Output: message.ActivityText ?? message.Text)));
            else if (message.Call is { } call)
            {
                TranscriptMessage? result = ResultFor(this.messages, call);
                bool failed = Failed(call, result);
                ToolCallItem card = new(call.Label, StatusFor(call, result), call.Arguments, failed ? null : OutputFor(call, result),
                    failed ? OutputFor(call, result) ?? "The tool reported a failure." : null);
                TranscriptOutput? matched = OutputForCall(call, result);
                TranscriptMessage? inspection = matched is not null && HasRichInspection(matched) ? result! with { Outputs = [matched] } : null;
                shown.Add(new(key, null, card, RunningProgress(call, result), inspection));
            }
            else if (message.Role == "tool")
            {
                if (HasCall(this.messages, message)) continue;
                TranscriptOutput[] unmatched = [.. message.Outputs.Where(output => !this.messages.Any(other => other.Call?.CallId == output.CallId))];
                bool failed = unmatched.Any(output => output.Failed);
                string outputText = InspectionText(unmatched, message.Text);
                ToolCallItem card = new("Tool", failed ? ToolCallStatus.Error : ToolCallStatus.Success,
                    Output: failed ? null : outputText, Error: failed ? outputText : null);
                shown.Add(new(key, null, card, null, unmatched.Any(HasRichInspection) ? message with { Outputs = unmatched } : null));
            }
            // Any other entry the transcript does not name, such as a kind added later, still shows its text here.
            else shown.Add(Card(key, new(ApplicationTitle, ToolCallStatus.Success, Output: message.Text)));
        }
        return shown;

        static Block Card(string key, ToolCallItem card) => new(key, null, card, null, null);
    }

    // A stored message's id names it; a call without one is named by its call id, then by its stored position. The
    // reasoning still arriving has none of these and is the stretch's one live reasoning block.
    private static string KeyOf(TranscriptMessage message, int index) =>
        message.Id is { } id ? id.ToString("N")
        : message.Call is { } call ? "call:" + call.CallId
        : message.Sequence is { } sequence ? string.Create(CultureInfo.InvariantCulture, $"sequence:{sequence}")
        : message.Role == TranscriptMessage.ReasoningRole ? "reasoning"
        : string.Create(CultureInfo.InvariantCulture, $"position:{index}");

    private Control Create(Block block)
    {
        Control control;
        if (block.Reasoning is { } text)
        {
            ConversationMarkdown reasoning = ConversationText.In(new ConversationMarkdown(this.context, text));
            reasoning.Classes.Add("activity-reasoning");
            control = reasoning;
        }
        else control = new WorkBlock(block, this.context);
        control.Classes.Add("tool-activity-block");
        // Copied text puts a blank line between the stretch's blocks.
        TextSelection.SetIsBlock(control, true);
        return control;
    }

    private static void Update(Control control, Block block)
    {
        if (control is ConversationMarkdown reasoning) reasoning.Text = block.Reasoning ?? string.Empty;
        else if (control is WorkBlock work) work.Show(block);
    }

    /// <summary>What one block of an open stretch shows: reasoning text, or a card with its running progress and its rich inspection.</summary>
    private sealed record Block(string Key, string? Reasoning, ToolCallItem? Card, TranscriptProgress? Progress, TranscriptMessage? Inspected);

    /// <summary>A card, the progress of a call still running, and a result's rich inspection; each part changes in place.</summary>
    private sealed class WorkBlock : StackPanel
    {
        private readonly TranscriptContext context;
        private readonly ToolCallCard card;
        private (StackPanel Panel, ProgressBar Bar, TextBlock Text)? progress;
        private Control? inspection;
        private TranscriptMessage? inspected;

        internal WorkBlock(Block block, TranscriptContext context)
        {
            this.context = context;
            this.Spacing = 6;
            this.card = new ToolCallCard(block.Card!, context);
            this.Children.Add(this.card);
            this.Show(block);
        }

        internal void Show(Block block)
        {
            this.card.Show(block.Card!);
            if (block.Progress is { } reported)
            {
                (StackPanel Panel, ProgressBar Bar, TextBlock Text) shown = this.progress ??= Progress();
                bool measured = reported.Total is double total && total > 0;
                shown.Bar.IsIndeterminate = !measured;
                if (measured)
                {
                    shown.Bar.Maximum = reported.Total!.Value;
                    shown.Bar.Value = Math.Min(reported.Value, reported.Total.Value);
                }
                shown.Text.Text = ProgressText(reported);
                if (!this.Children.Contains(shown.Panel)) this.Children.Insert(1, shown.Panel);
            }
            else if (this.progress is { } ended) this.Children.Remove(ended.Panel);
            if (ReferenceEquals(block.Inspected, this.inspected)) return;
            if (this.inspection is not null) this.Children.Remove(this.inspection);
            this.inspected = block.Inspected;
            if (block.Inspected is { } holder)
            {
                StackPanel inspections = new() { Spacing = 6 };
                foreach (TranscriptOutput tool in holder.Outputs.Where(HasRichInspection))
                    inspections.Children.Add(ToolOutput.Inspection(tool, holder.Previews, this.context));
                this.inspection = inspections.Children.Count > 0 ? inspections : null;
            }
            else this.inspection = null;
            if (this.inspection is not null) this.Children.Add(this.inspection);
        }

        private static (StackPanel Panel, ProgressBar Bar, TextBlock Text) Progress()
        {
            ProgressBar bar = new();
            TextBlock text = Forms.Text(string.Empty, "tool-progress-text");
            StackPanel status = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            status.Classes.Add("tool-progress");
            status.Children.Add(bar);
            status.Children.Add(text);
            return (status, bar, text);
        }
    }
}
