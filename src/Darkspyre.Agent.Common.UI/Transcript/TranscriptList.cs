using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Errors;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>
/// An open edit of a queued prompt: the text being typed, the indexes of the attachments kept and the id of the hold the
/// session gave it. <paramref name="Ended"/> is true once a save found that hold gone.
/// </summary>
internal sealed record QueuedDraft(string Text, IReadOnlyList<int> Kept, object? Handle, string OriginalText, IReadOnlyList<int> OriginalKept, bool Ended = false);

/// <summary>The kind of a transcript row.</summary>
public enum TranscriptRowKind
{
    /// <summary>A prompt, reply or other stored message.</summary>
    Message,

    /// <summary>A stretch of work between replies.</summary>
    Activity,

    /// <summary>The reply still arriving.</summary>
    Streaming,

    /// <summary>A prompt waiting in the queue.</summary>
    Queued,
}

/// <summary>
/// One row of a transcript, kept by its key while it stays in the transcript. Its view redraws on <see cref="Changed"/>;
/// whether a work stretch is open lives here, so a row scrolled out of view and back keeps it. The reply still streaming
/// becomes the stored reply's row when that reply is stored, so its view stays.
/// </summary>
public sealed class TranscriptRow(string key, TranscriptRowKind kind)
{
    /// <summary>The row's stable name within its conversation, which the scroll anchor records.</summary>
    public string Key { get; private set; } = key;

    /// <summary>What the row shows.</summary>
    public TranscriptRowKind Kind { get; private set; } = kind;

    /// <summary>The response a streaming row shows, which the stored reply carrying it takes over; null for every other row.</summary>
    internal Guid? ResponseId { get; set; }

    /// <summary>Counts the changes to what the row shows, so a view draws again only for a change it has not drawn.</summary>
    internal int Version { get; private set; }

    /// <summary>The absolute position of the row's first message; <see cref="int.MaxValue"/> for the streaming reply and queued prompts.</summary>
    internal int Position { get; set; }

    /// <summary>The stored message of a message row.</summary>
    public TranscriptMessage? Message { get; private set; }

    /// <summary>The messages of a work stretch.</summary>
    public IReadOnlyList<TranscriptMessage> Activity { get; private set; } = [];

    /// <summary>Whether the turn still works on this stretch.</summary>
    public bool Live { get; private set; }

    /// <summary>The runtime's latest progress on a live stretch.</summary>
    public string Progress { get; private set; } = string.Empty;

    /// <summary>The text of the reply still arriving.</summary>
    public string Streaming { get; private set; } = string.Empty;

    /// <summary>The queued prompt of a queued row.</summary>
    public TranscriptWaiting? Waiting { get; private set; }

    /// <summary>Why the last steer of the queued prompt was refused, or null.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Whether the queued prompt can be steered into the running turn.</summary>
    public bool CanSteer { get; private set; }

    /// <summary>Whether the running turn's steer takes a queued prompt's attachments.</summary>
    public bool CanSteerFiles { get; private set; }

    /// <summary>Whether a work stretch is open.</summary>
    public bool Expanded { get; set; }

    /// <summary>The open edit of a queued row, or null; a change redraws the row.</summary>
    internal QueuedDraft? Editing
    {
        get => this.editing;
        set => this.Set(this.editing == value, () => this.editing = value);
    }

    private QueuedDraft? editing;

    // A removed row or released pane cannot accept a late queue hold.
    internal bool Available { get; set; } = true;

    /// <summary>Raised when what the row shows changes.</summary>
    public event EventHandler? Changed;

    internal void ShowMessage(TranscriptMessage message) => this.Set(this.Message == message, () => this.Message = message);

    internal void ShowActivity(IReadOnlyList<TranscriptMessage> messages, bool live, string progress) =>
        this.Set(this.Activity.SequenceEqual(messages) && this.Live == live && this.Progress == progress, () =>
        {
            this.Activity = messages;
            this.Live = live;
            this.Progress = progress;
        });

    internal void ShowStreaming(string text) => this.Set(this.Streaming == text, () => this.Streaming = text);

    /// <summary>The streaming row becomes the row of its stored reply, named <paramref name="messageKey"/>; the reply's message fills it next.</summary>
    internal void Adopt(string messageKey)
    {
        this.Key = messageKey;
        this.Kind = TranscriptRowKind.Message;
        this.ResponseId = null;
    }

    internal void ShowQueued(TranscriptWaiting prompt, string? refusal, bool canSteer, bool canSteerFiles) =>
        this.Set(this.Waiting == prompt && this.Refusal == refusal && this.CanSteer == canSteer && this.CanSteerFiles == canSteerFiles, () =>
        {
            this.Waiting = prompt;
            this.Refusal = refusal;
            this.CanSteer = canSteer;
            this.CanSteerFiles = canSteerFiles;
        });

    private void Set(bool same, Action change)
    {
        if (same) return;
        change();
        this.Version++;
        this.Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The rows of one conversation's transcript, every one drawn at its real height in a plain stack: prompts, replies, tool
/// output and collapsed work stretches, then the reply still streaming, then the prompts waiting in the queue. The
/// session bounds the rows to its shown window, so drawing all of them stays cheap. Rows are keyed, so an update
/// changes only the rows whose content changed, and a streaming reply updates only its own Markdown view's text. The
/// stored reply takes over the streaming row of its response, so its Markdown view stays.
/// </summary>
public sealed class TranscriptList : UserControl
{
    private const string StreamingKey = "streaming";
    private const string NoLongerWaitingNotice = "This message is no longer waiting in the queue.";
    private readonly TranscriptContext context;
    private readonly TranscriptActions actions;
    private readonly ObservableCollection<TranscriptRow> rows = [];

    /// <summary>Creates the list over <paramref name="context"/>.</summary>
    public TranscriptList(TranscriptContext context, TranscriptActions actions)
    {
        this.context = context;
        this.actions = actions;
        this.Items = new ItemsControl
        {
            ItemsSource = this.rows,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
            // A row whose content fails shows a row failure card; every other row of the transcript still draws.
            ItemTemplate = new FuncDataTemplate<TranscriptRow>((row, _) => row is null ? null
                : new UiRegion(row.Kind == TranscriptRowKind.Queued ? "Queued prompt" : "Transcript row",
                    () => new TranscriptRowView(row, this.context, this.actions)), supportsRecycling: false),
        };
        this.Items.Classes.Add("transcript-content");
        this.Content = this.Items;
    }

    /// <summary>The list control, which the transcript scroll reads rows from.</summary>
    public ItemsControl Items { get; }

    /// <summary>The rows shown, in order.</summary>
    public IReadOnlyList<TranscriptRow> Rows => this.rows;

    /// <summary>A row's stable name within its conversation, which the scroll anchor records.</summary>
    internal static string RowId(object key) => key is ValueTuple<string, int> (string kind, int index)
        ? string.Create(CultureInfo.InvariantCulture, $"{kind}-{index}")
        : throw new ArgumentException("A transcript row key is a kind and an index.", nameof(key));

    /// <summary>
    /// Shows <paramref name="messages"/>, of which <paramref name="offset"/> stored turns come before the first.
    /// <paramref name="reasoning"/> still arriving joins the last work stretch; <paramref name="busy"/> marks it live
    /// with <paramref name="progress"/>. <paramref name="streaming"/> shows as a last reply until a stored message
    /// carries <paramref name="activeResponseId"/>. <paramref name="waiting"/> are the queued prompts, with the reason
    /// each last steer was refused. <paramref name="canSteerFiles"/> says whether that steer takes a prompt's
    /// attachments, so a prompt with files offers the action only where they would go with it.
    /// </summary>
    public void Show(IReadOnlyList<TranscriptMessage> messages, string reasoning, bool busy, int offset, string streaming, string progress,
        Guid? activeResponseId, IReadOnlyList<TranscriptWaiting> waiting, IReadOnlyDictionary<Guid, string> refusals, bool canSteer,
        bool canSteerFiles)
    {
        List<(string Key, TranscriptRowKind Kind, Action<TranscriptRow> Fill)> wanted = [];
        // The stored replies by the response they finish, for the streaming row that showed them.
        Dictionary<Guid, string> replies = [];
        foreach (TranscriptLayout.Row row in TranscriptLayout.Build(messages, reasoning, busy, offset))
        {
            if (row.Activity is { } activity)
            {
                // Only the stretch the turn is working on now shimmers.
                List<TranscriptMessage> shown = [.. activity];
                bool live = row.Live;
                int position = row.Position;
                wanted.Add((RowId(row.Key), TranscriptRowKind.Activity, item =>
                {
                    item.Position = position;
                    item.ShowActivity(shown, live, live ? progress : string.Empty);
                }));
            }
            else
            {
                TranscriptMessage message = row.Message!;
                int position = row.Position;
                wanted.Add((RowId(row.Key), TranscriptRowKind.Message, item =>
                {
                    item.Position = position;
                    item.ShowMessage(message);
                }));
                if (message is { Role: "assistant", ResponseId: { } finished }) replies.TryAdd(finished, RowId(row.Key));
            }
        }
        string visible = activeResponseId is { } response && messages.Any(message => message.ResponseId == response) ? string.Empty : streaming;
        if (visible.Length > 0)
            wanted.Add((StreamingKey + ":" + activeResponseId, TranscriptRowKind.Streaming, item =>
            {
                item.ResponseId = activeResponseId;
                item.Position = int.MaxValue;
                item.ShowStreaming(visible);
            }));
        foreach (TranscriptWaiting prompt in waiting)
            wanted.Add((prompt.PromptId.ToString("D"), TranscriptRowKind.Queued, item =>
            {
                item.Position = int.MaxValue;
                item.ShowQueued(prompt, refusals.GetValueOrDefault(prompt.PromptId), canSteer, canSteerFiles);
            }));
        foreach (TranscriptRow retained in this.rows.Where(row => row.Kind == TranscriptRowKind.Queued && row.Editing is not null && row.Waiting is not null
            && waiting.All(prompt => prompt.PromptId != row.Waiting.PromptId)))
        {
            TranscriptWaiting prompt = retained.Waiting!;
            wanted.Add((retained.Key, TranscriptRowKind.Queued, item => item.ShowQueued(prompt, NoLongerWaitingNotice, false, false)));
        }
        // Rows no longer shown leave first, so the rows that stay are never moved past them: a moved row gets a new
        // view, which the pane's scroll anchoring cannot hold in place. A work stretch at the top of the window is
        // renamed when older messages that continue it load, so a load always leaves one such row.
        HashSet<(string Key, TranscriptRowKind Kind)> shownKeys = [.. wanted.Select(item => (item.Key, item.Kind))];
        // A stored reply takes over the streaming row of its response, so the reply keeps the streaming row's view.
        foreach (TranscriptRow streamed in this.rows.Where(row => row.Kind == TranscriptRowKind.Streaming && !shownKeys.Contains((row.Key, row.Kind))).ToArray())
            if (streamed.ResponseId is { } streamedResponse && replies.TryGetValue(streamedResponse, out string? reply)
                && this.rows.All(row => row.Key != reply || row.Kind != TranscriptRowKind.Message))
                streamed.Adopt(reply);
        for (int index = this.rows.Count - 1; index >= 0; index--)
            if (!shownKeys.Contains((this.rows[index].Key, this.rows[index].Kind)))
            {
                this.rows[index].Available = false;
                this.rows.RemoveAt(index);
            }
        for (int index = 0; index < wanted.Count; index++)
        {
            (string key, TranscriptRowKind kind, Action<TranscriptRow> fill) = wanted[index];
            int found = -1;
            for (int look = index; look < this.rows.Count; look++)
            {
                if (this.rows[look].Key != key || this.rows[look].Kind != kind) continue;
                found = look;
                break;
            }
            if (found < 0)
            {
                TranscriptRow created = new(key, kind);
                fill(created);
                this.rows.Insert(index, created);
                continue;
            }
            if (found != index) this.rows.Move(found, index);
            fill(this.rows[index]);
        }
    }

    /// <summary>Empties the list, as when the conversation it shows changes.</summary>
    public void Clear()
    {
        this.ReleaseRows();
        this.rows.Clear();
    }

    internal void ReleaseRows()
    {
        foreach (TranscriptRow row in this.rows) row.Available = false;
    }
}

/// <summary>
/// One row's view: it draws the row once and changes the part a row change touched, while it is in a window. A change
/// made while the view is out of its window is drawn when it returns.
/// </summary>
public sealed class TranscriptRowView : UserControl
{
    private readonly TranscriptRow row;
    private readonly TranscriptContext context;
    private readonly TranscriptActions actions;
    private ToolActivity? activity;
    private MessageView? message;
    private QueuedView? queued;
    private ScrollViewer? pane;
    // The row version last drawn.
    private int drawn;

    /// <summary>Creates the view of <paramref name="row"/>.</summary>
    public TranscriptRowView(TranscriptRow row, TranscriptContext context, TranscriptActions actions)
    {
        this.row = row;
        this.context = context;
        this.actions = actions;
        // Copied text puts a blank line between messages.
        TextSelection.SetIsBlock(this, true);
        this.Render();
    }

    /// <summary>The row shown.</summary>
    public TranscriptRow Row => this.row;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.row.Changed += this.OnChanged;
        if (this.drawn != this.row.Version) this.Render();
        // The pane's scroll anchoring keeps the row at the top of the view in place while rows above it load, release
        // or change size; it considers only the rows that register.
        this.pane = this.FindAncestorOfType<ScrollViewer>();
        this.pane?.RegisterAnchorCandidate(this);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        this.row.Changed -= this.OnChanged;
        // A row left registered after it leaves the pane fails the pane's next arrange.
        this.pane?.UnregisterAnchorCandidate(this);
        this.pane = null;
    }

    private void OnChanged(object? sender, EventArgs e) => this.Render();

    private void Render()
    {
        this.drawn = this.row.Version;
        switch (this.row.Kind)
        {
            case TranscriptRowKind.Activity:
                if (this.activity is null)
                {
                    this.activity = new ToolActivity(this.context);
                    Expander expander = (Expander)this.activity.Content!;
                    expander.IsExpanded = this.row.Expanded;
                    expander.PropertyChanged += (_, args) =>
                    {
                        if (args.Property == Expander.IsExpandedProperty) this.row.Expanded = expander.IsExpanded;
                    };
                    this.Content = this.activity;
                }
                this.activity.Update(this.row.Activity, this.row.Live, this.row.Progress);
                break;
            case TranscriptRowKind.Streaming:
                // A streaming reply updates only its own Markdown view's text.
                if (this.message is null) this.Content = (this.message = new MessageView(this, new TranscriptMessage("assistant", this.row.Streaming, null, null, null, false, null, [], [], null, null), true)).Root;
                else this.message.ShowStreaming(this.row.Streaming);
                break;
            case TranscriptRowKind.Queued:
                TranscriptWaiting prompt = this.row.Waiting!;
                if (this.queued is null || this.queued.Shape != QueuedView.ShapeOf(prompt))
                    this.Content = (this.queued = new QueuedView(this, prompt)).Root;
                this.queued.Show(prompt, this.row.Refusal, this.row.CanSteer, this.row.CanSteerFiles, this.row.Editing);
                break;
            default:
                // The stored reply that took over a streaming row keeps the streaming row's view.
                TranscriptMessage stored = this.row.Message!;
                if (this.message is null || this.message.Shape != MessageView.ShapeOf(this, stored))
                    this.Content = (this.message = new MessageView(this, stored, false)).Root;
                else this.message.Show(stored);
                break;
        }
    }

    // The edit action of a user turn: it asks the row's turn again, differently.
    private Func<Task>? EditFor(TranscriptMessage turn) => this.actions.Edit is { } edit && turn is { Id: not null, CanAskAgain: true }
        ? () => this.row.Message?.Id is { } turnId ? edit(turnId) : Task.CompletedTask : null;

    private static StackPanel Actions(Func<string> text, Func<Task>? edit)
    {
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Classes.Add("message-actions");
        if (edit is not null) actions.Children.Add(IconAction(AppIcons.Edit, "Edit prompt", "Ask this again, differently", edit));
        // The copy button gives the message's Markdown source and shows a check for two seconds.
        Button copy = null!;
        copy = IconAction(AppIcons.Copy, "Copy", "Copy", () => CopyFeedback.CopyAsync(copy, text()));
        actions.Children.Add(copy);
        return actions;
    }

    private static Button IconAction(IconSource icon, string name, string tip, Func<Task> click)
    {
        Button button = Forms.Button(name, "ghost", click, ButtonSize.ExtraSmall);
        button.Content = new Icon { Source = icon, Width = 14, Height = 14 };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    // A prompt keeps its line breaks; the transcript's selection selects it.
    private static TextBlock Prompt(string text) => ConversationText.In(Forms.Text(text, "prompt-text"));

    // A user turn, and a composer command the user ran, is a bubble at the right, at most three quarters of the width;
    // every other turn spans the transcript.
    private static Border Turn(string role, Control body, Control? actions, string? state = null)
    {
        Border bubble = new() { Child = body };
        bubble.Classes.Add("turn-bubble");
        StackPanel layout = new() { Spacing = 4 };
        layout.Children.Add(bubble);
        if (actions is not null) layout.Children.Add(actions);
        Border turn = new() { Child = layout };
        turn.Classes.Add("turn");
        turn.Classes.Add(role);
        if (role == TranscriptMessage.CommandRole) turn.Classes.Add("user");
        if (state is not null) turn.Classes.Add(state);
        if (role is "user" or TranscriptMessage.CommandRole)
        {
            bubble.HorizontalAlignment = HorizontalAlignment.Right;
            turn.SizeChanged += (_, args) => bubble.MaxWidth = Math.Max(0, args.NewSize.Width * 0.75);
        }
        return turn;
    }

    // Places the previews part of a body before <before>, or after its other parts: replaced only when the previews changed,
    // removed when there are none.
    private static (Control? View, IReadOnlyList<TranscriptPreview> Shown) Previews(StackPanel body, Control? view,
        IReadOnlyList<TranscriptPreview>? shown, IReadOnlyList<TranscriptPreview> previews, Func<Control?> make, Control? before = null)
    {
        if (shown is not null && shown.SequenceEqual(previews)) return (view, shown);
        if (view is not null) body.Children.Remove(view);
        Control? made = make();
        if (made is not null) body.Children.Insert(before is null ? body.Children.Count : body.Children.IndexOf(before), made);
        return (made, previews);
    }

    /// <summary>
    /// A stored message, or the reply still streaming, as one turn whose parts change in place: its text, its previews and
    /// its tool output. A change of what parts the turn has draws the turn again.
    /// </summary>
    private sealed class MessageView
    {
        private readonly TranscriptRowView owner;
        private readonly StackPanel body = new() { Spacing = 6 };
        private readonly ConversationMarkdown? markdown;
        private readonly TextBlock? prompt;
        private Control? tool;
        private IReadOnlyList<TranscriptOutput>? toolShown;
        private StackPanel? documents;
        private Control? previews;
        private IReadOnlyList<TranscriptPreview>? previewsShown;
        private string text;

        internal MessageView(TranscriptRowView owner, TranscriptMessage message, bool streaming)
        {
            this.owner = owner;
            this.Shape = ShapeOf(owner, message);
            this.text = message.Text;
            TranscriptContext context = owner.context;
            if (message.Outputs.Count > 0)
            {
                this.body.Children.Add(Forms.Text(message.Role, "turn-source"));
                this.ShowTool(message);
            }
            else if (message.Role is "user" or TranscriptMessage.CommandRole)
            {
                if (message.Text.Length > 0) this.body.Children.Add(this.prompt = Prompt(message.Text));
            }
            else if (message.Role == "assistant")
            {
                if (message.Text.Length > 0)
                    this.body.Children.Add(this.markdown = ConversationText.In(new ConversationMarkdown(context, message.Text, streaming: true) { Classes = { "body" } }));
            }
            else
            {
                this.body.Children.Add(Forms.Text(message.Role, "turn-source"));
                this.body.Children.Add(this.markdown = ConversationText.In(new ConversationMarkdown(context, message.Text) { Classes = { "body" } }));
            }
            if (message.Outputs.Count == 0 && message.Role is "user" or "assistant") this.ShowPreviews(message);
            this.ShowDocuments(message);
            Control? actions = message.Role switch
            {
                "user" => Actions(() => this.text, owner.EditFor(message)),
                // A command line has no fork checkpoint, so it has no edit.
                TranscriptMessage.CommandRole => Actions(() => this.text, null),
                "assistant" when message.Text.Length > 0 => Actions(() => this.text, null),
                _ => null,
            };
            this.Root = Turn(message.Role, this.body, actions, streaming ? "streaming" : null);
        }

        internal Border Root { get; }

        /// <summary>The parts the turn has; a message with other parts needs another turn.</summary>
        internal string Shape { get; }

        internal static string ShapeOf(TranscriptRowView owner, TranscriptMessage message) =>
            $"{message.Role}|{message.Outputs.Count > 0}|{message.Text.Length > 0}|{message.Role == "user" && owner.EditFor(message) is not null}";

        internal void Show(TranscriptMessage message)
        {
            this.Root.Classes.Remove("streaming");
            this.text = message.Text;
            if (this.markdown is { } reply && reply.Text != message.Text) reply.Text = message.Text;
            if (this.prompt is { } sent && sent.Text != message.Text) sent.Text = message.Text;
            if (message.Outputs.Count > 0) this.ShowTool(message);
            else if (message.Role is "user" or "assistant") this.ShowPreviews(message);
            this.ShowDocuments(message);
        }

        internal void ShowStreaming(string streamed)
        {
            this.text = streamed;
            this.markdown!.Text = streamed;
        }

        private void ShowTool(TranscriptMessage message)
        {
            if (this.toolShown is not null && this.toolShown.SequenceEqual(message.Outputs)) return;
            int at = this.tool is null ? this.body.Children.Count : this.body.Children.IndexOf(this.tool);
            if (this.tool is not null) this.body.Children.Remove(this.tool);
            StackPanel outputs = new() { Spacing = 8 };
            foreach (TranscriptOutput output in message.Outputs) outputs.Children.Add(ToolOutput.Of(output, true, message.Previews, this.owner.context));
            this.tool = outputs;
            this.toolShown = message.Outputs;
            this.body.Children.Insert(at, outputs);
        }

        private void ShowDocuments(TranscriptMessage message)
        {
            if (message.Documents.Count == 0)
            {
                if (this.documents is not null) this.documents.IsVisible = false;
                return;
            }
            if (this.documents is null) this.body.Children.Add(this.documents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 });
            this.documents.IsVisible = true;
            this.documents.Children.Clear();
            foreach (TranscriptDocumentAction document in message.Documents)
            {
                Button button = null!;
                button = Forms.Button(document.Name, "ghost", () => this.owner.context.OpenLink(document.Target, button), ButtonSize.ExtraSmall);
                button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new Icon { Source = document.Icon, Width = 14, Height = 14 }, Forms.Text(document.Name) } };
                Avalonia.Automation.AutomationProperties.SetName(button, document.Name);
                this.documents.Children.Add(button);
            }
        }

        private void ShowPreviews(TranscriptMessage message) => (this.previews, this.previewsShown) = Previews(this.body, this.previews, this.previewsShown,
            message.Previews, () => AttachmentPreviewList.Of(message.Previews, this.owner.context.Images));
    }

    /// <summary>
    /// A prompt waiting in the queue: its text, previews, why its last steer was refused, and its actions, each changed in
    /// place. While the row's <see cref="TranscriptRow.Editing"/> is set it shows an edit box in their place: the text,
    /// the kept attachments with a remove button each, and Save and Cancel.
    /// </summary>
    private sealed class QueuedView
    {
        private const string NoLongerWaiting = "This message is no longer waiting in the queue.";
        private const string EditedElsewhere = "This message is being edited on another device.";
        private const string HoldEndedNotice = "This edit ended before it was saved. Choose Edit again to save it.";
        private readonly TranscriptRowView owner;
        private readonly Guid promptId;
        private readonly StackPanel body = new() { Spacing = 6 };
        private readonly TextBlock? prompt;
        private readonly TextBlock refusal = Forms.Text(string.Empty, "queued-notice");
        private readonly Button? steer;
        private readonly Button? edit;
        private Control? previews;
        private IReadOnlyList<TranscriptPreview>? previewsShown;
        private TranscriptWaiting waiting;
        private StackPanel? editor;
        private TextBox? editText;
        private WrapPanel? editAttachments;
        private TextBlock? endedNotice;
        private Button? save;
        private Button? editAgain;
        private IReadOnlyList<int>? keptShown;

        internal QueuedView(TranscriptRowView owner, TranscriptWaiting waiting)
        {
            this.owner = owner;
            this.waiting = waiting;
            this.promptId = waiting.PromptId;
            this.Shape = ShapeOf(waiting);
            this.body.Children.Add(Forms.Text("Queued", "queued-state"));
            if (waiting.Text.Length > 0) this.body.Children.Add(this.prompt = Prompt(waiting.Text));
            this.body.Children.Add(this.refusal);
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Classes.Add("message-actions");
            Guid promptId = waiting.PromptId;
            if (owner.actions.Steer is { } steerNow)
            {
                this.steer = Forms.Button("steer now", "ghost", () => steerNow(promptId), ButtonSize.ExtraSmall);
                ToolTip.SetTip(this.steer, "Send this to the agent now, inside the running turn");
                actions.Children.Add(this.steer);
            }
            if (owner.actions.BeginEdit is not null && owner.actions.SaveEdit is not null && owner.actions.CancelEdit is not null)
            {
                this.edit = IconAction(AppIcons.Edit, "Edit queued message", "Edit this message before the agent takes it", () => this.BeginAsync());
                actions.Children.Add(this.edit);
            }
            if (owner.actions.Remove is { } remove)
                actions.Children.Add(IconAction(AppIcons.Close, "Remove from queue", "Remove this message from the queue", () => remove(promptId)));
            this.Root = Turn("user", this.body, actions, "queued");
        }

        internal Border Root { get; }

        /// <summary>The parts the queued turn has; a prompt with other parts needs another turn.</summary>
        internal string Shape { get; }

        internal static string ShapeOf(TranscriptWaiting waiting) => (waiting.Text.Length > 0).ToString();

        internal void Show(TranscriptWaiting waiting, string? refused, bool canSteer, bool canSteerFiles, QueuedDraft? editing)
        {
            this.waiting = waiting;
            if (this.prompt is { } sent && sent.Text != waiting.Text) sent.Text = waiting.Text;
            // The previews sit after the prompt text and before the refusal.
            (this.previews, this.previewsShown) = Previews(this.body, this.previews, this.previewsShown, waiting.Previews,
                () => AttachmentPreviewList.Of(waiting.Previews, this.owner.context.Images), this.refusal);
            this.refusal.IsVisible = refused is not null && editing is null;
            this.refusal.Text = refused;
            // A prompt with attachments is steerable only where the running turn's steer carries them.
            if (this.steer is not null)
                this.steer.IsVisible = canSteer && (waiting.Previews.Count == 0 || canSteerFiles) && editing is null;
            if (this.edit is not null) this.edit.IsVisible = editing is null;
            if (this.prompt is not null) this.prompt.IsVisible = editing is null;
            if (this.previews is not null) this.previews.IsVisible = editing is null;
            if (editing is null)
            {
                if (this.editor is not null) this.body.Children.Remove(this.editor);
                this.editor = null;
                this.editText = null;
                this.editAttachments = null;
                this.endedNotice = null;
                this.save = null;
                this.editAgain = null;
                this.keptShown = null;
                return;
            }
            if (this.editor is null) this.body.Children.Insert(this.body.Children.IndexOf(this.refusal), this.editor = this.Editor(editing));
            this.endedNotice!.IsVisible = editing.Ended;
            this.save!.IsVisible = !editing.Ended;
            this.editAgain!.IsVisible = editing.Ended;
            if (this.editText!.Text != editing.Text) this.editText.Text = editing.Text;
            if (this.keptShown is null || !this.keptShown.SequenceEqual(editing.Kept)) this.ShowKept(editing.Kept);
        }

        // The edit box: the text, where Enter starts a new line and Escape cancels, the kept attachments, Save and Cancel.
        private StackPanel Editor(QueuedDraft editing)
        {
            StackPanel editor = new() { Spacing = 6 };
            editor.Classes.Add("queued-editor");
            TextBox text = this.editText = new() { Text = editing.Text, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            text.Classes.Add("queued-edit-text");
            Avalonia.Automation.AutomationProperties.SetName(text, "Queued message");
            text.TextChanged += (_, _) =>
            {
                if (this.owner.row.Editing is { } open && open.Text != (text.Text ?? string.Empty))
                    this.owner.row.Editing = open with { Text = text.Text ?? string.Empty };
            };
            text.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, async (_, args) =>
            {
                if (args.Key != Avalonia.Input.Key.Escape) return;
                args.Handled = true;
                try { await this.CancelAsync(); }
                catch (Exception error) when (error is not OperationCanceledException) { UiRegion.Report(text, error); }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            editor.Children.Add(text);
            editor.Children.Add(this.editAttachments = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 });
            editor.Children.Add(this.endedNotice = Forms.Text(HoldEndedNotice, "queued-notice"));
            this.endedNotice.IsVisible = editing.Ended;
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Forms.Button("Cancel", "ghost", this.CancelAsync, ButtonSize.ExtraSmall));
            buttons.Children.Add(this.save = Forms.Button("Save", "primary", this.SaveAsync, ButtonSize.ExtraSmall));
            buttons.Children.Add(this.editAgain = Forms.Button("Edit again", "primary", this.EditAgainAsync, ButtonSize.ExtraSmall));
            this.save.IsVisible = !editing.Ended;
            this.editAgain.IsVisible = editing.Ended;
            editor.Children.Add(buttons);
            Dispatcher.UIThread.Post(() => text.Focus(), DispatcherPriority.Loaded);
            return editor;
        }

        // Each kept attachment's preview with a button that drops it from the edit. Attachments can only be removed.
        private void ShowKept(IReadOnlyList<int> kept)
        {
            this.keptShown = kept;
            WrapPanel list = this.editAttachments!;
            list.Children.Clear();
            list.IsVisible = kept.Count > 0;
            foreach (int index in kept)
            {
                if (this.waiting.Previews.FirstOrDefault(preview => preview.Position == index) is not { } preview) continue;
                StackPanel item = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
                item.Children.Add(new AttachmentPreviewView(preview, AttachmentPreviewSize.Thumbnail, this.owner.context.Images));
                item.Children.Add(IconAction(AppIcons.Close, "Remove attachment", $"Remove {preview.Name} from this message", () =>
                {
                    if (this.owner.row.Editing is { } open) this.owner.row.Editing = open with { Kept = [.. open.Kept.Where(position => position != index)] };
                    return Task.CompletedTask;
                }));
                list.Children.Add(item);
            }
        }

        // The row becomes an edit box only once the session holds the prompt. Edit again begins a new hold for the text the
        // person typed and reloads the message's attachments from the queue, whose positions may have changed.
        private async Task BeginAsync(string? typed = null)
        {
            TranscriptWaiting shown = this.waiting;
            Func<Guid, object?, Task> cancel = this.owner.actions.CancelEdit!;
            QueueBegin start = await this.owner.actions.BeginEdit!(this.promptId);
            if (!this.owner.row.Available)
            {
                if (start.Status == QueueBeginStatus.Held)
                    await TranscriptPane.CancelAsync(this.owner.context, cancel, this.promptId, start.Handle);
                return;
            }
            switch (start.Status)
            {
                case QueueBeginStatus.Held:
                    int[] kept = [.. shown.Previews.Select(preview => preview.Position)];
                    this.owner.row.Editing = new QueuedDraft(typed ?? shown.Text, kept, start.Handle, shown.Text, kept);
                    break;
                case QueueBeginStatus.EditedElsewhere:
                    await this.EndEditAsync(EditedElsewhere);
                    break;
                default:
                    await this.EndEditAsync(NoLongerWaiting);
                    break;
            }
        }

        private async Task SaveAsync()
        {
            if (this.owner.row.Editing is not { } open) return;
            string text = this.editText?.Text ?? open.Text;
            QueueSaveStatus saved = await this.owner.actions.SaveEdit!(this.promptId, open.Handle, text, open.Kept);
            switch (saved)
            {
                case QueueSaveStatus.Saved:
                    this.owner.row.Editing = null;
                    break;
                case QueueSaveStatus.HoldEnded:
                    // The editor stays open with the person's text; Save is replaced by Edit again.
                    if (this.owner.row.Editing is { } now) this.owner.row.Editing = now with { Text = text, Ended = true };
                    break;
                default:
                    await this.EndEditAsync(NoLongerWaiting);
                    break;
            }
        }

        private Task EditAgainAsync() => this.owner.row.Editing is { } open ? this.BeginAsync(this.editText?.Text ?? open.Text) : Task.CompletedTask;

        private async Task CancelAsync()
        {
            if (this.owner.row.Editing is not { } open) return;
            bool dirty = open.Text != open.OriginalText || !open.Kept.SequenceEqual(open.OriginalKept);
            if (this.owner.actions.ConfirmDiscard is { } confirm && !await confirm(dirty)) return;
            this.owner.row.Editing = null;
            await this.owner.actions.CancelEdit!(this.promptId, open.Handle);
        }

        private async Task EndEditAsync(string message)
        {
            await this.owner.context.Warn(message);
        }
    }
}
