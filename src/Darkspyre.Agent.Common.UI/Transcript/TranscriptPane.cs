using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>One owner's transcript list, selection, scrolling and lazy image leases.</summary>
public sealed class TranscriptPane
{
    private readonly TextBlock older = Forms.Text("Loading earlier turns", "transcript-older-loading");
    private readonly TranscriptContext context;
    private readonly TranscriptActions actions;
    private readonly Action<TranscriptAnchor?> scrolled;
    private readonly Func<CancellationToken, Task<bool>> loadOlder;
    private readonly Action release;
    private readonly TranscriptAnchor? start;
    private readonly NewestButton newest;

    /// <summary>Creates a transcript pane with owner-provided state and operations.</summary>
    public TranscriptPane(Guid sessionId, TranscriptContext context, TranscriptActions actions, TranscriptAnchor? start,
        Action<TranscriptAnchor?> scrolled, Func<CancellationToken, Task<bool>> loadOlder, Action release)
    {
        this.SessionId = sessionId;
        this.context = context;
        this.actions = actions;
        this.start = start;
        this.scrolled = scrolled;
        this.loadOlder = async cancellationToken =>
        {
            try { return await loadOlder(cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await context.Warn($"Earlier transcript turns could not be loaded. {error.Message}");
                return false;
            }
        };
        this.release = release;
        this.List = new TranscriptList(context, actions);
        this.older.IsVisible = false;
        StackPanel content = new();
        content.Classes.Add("transcript-messages");
        content.Children.Add(this.older);
        content.Children.Add(this.List);
        ScrollViewer pane = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = content };
        TextSelectionScope selection = new() { Child = pane };
        this.newest = new NewestButton(() => this.Scroll!.Latest());
        this.Scroll = new TranscriptScroll(pane, this.newest, selection);
        Grid layers = new();
        layers.Children.Add(selection);
        layers.Children.Add(this.newest);
        this.View = layers;
    }

    /// <summary>The owner's session identity.</summary>
    public Guid SessionId { get; }
    /// <summary>The pane's root control.</summary>
    public Control View { get; }
    /// <summary>The shared transcript rows.</summary>
    public TranscriptList List { get; }
    /// <summary>The pane's scroll controller.</summary>
    public TranscriptScroll Scroll { get; }
    /// <summary>Shows or hides the pane without detaching it.</summary>
    public bool Shown { get => this.View.IsVisible; set => this.View.IsVisible = value; }
    /// <summary>Whether the conversation is working.</summary>
    public bool Working { set => this.newest.Working = value; }
    /// <summary>Whether the older-turn loading line is shown.</summary>
    public bool LoadingOlder { set => this.older.IsVisible = value; }
    /// <summary>Places the pane at its initial anchor or newest message.</summary>
    public void Place() => this.Scroll.Attach(this.List, this.SessionId.ToString("D"), this.start, this.scrolled, this.loadOlder, this.release);

    /// <summary>Releases the pane's existing row edits, scrolling and image leases.</summary>
    public void Release()
    {
        if (this.actions.CancelEdit is { } cancel)
            foreach (TranscriptRow row in this.List.Rows)
                if (row is { Editing: { } editing, Waiting: { } waiting }) _ = this.CancelAsync(cancel, waiting.PromptId, editing.Handle);
        this.Scroll.Detach();
        this.context.Images.Dispose();
    }

    private async Task CancelAsync(Func<Guid, object?, Task> cancel, Guid promptId, object? handle)
    {
        try { await cancel(promptId, handle); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await this.context.Warn($"The edit of a queued message could not be cancelled. {error.Message}");
        }
    }
}
