using Avalonia;
using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>Hosts the owner's replaceable Markdown binding for one transcript body.</summary>
public sealed class ConversationMarkdown : ContentControl
{
    private readonly Func<string, bool, ITranscriptMarkdown> create;
    private ITranscriptMarkdown? binding;
    private string text;
    private bool streaming;

    /// <summary>Creates a Markdown shell with its owner-provided binding.</summary>
    public ConversationMarkdown(TranscriptContext context, string text = "", bool streaming = false)
    {
        this.create = context.Markdown;
        this.text = text;
        this.streaming = streaming;
    }

    /// <summary>The literal Markdown text.</summary>
    public string Text
    {
        get => this.text;
        set { this.text = value ?? string.Empty; if (this.binding is { } current) current.Text = this.text; }
    }

    /// <summary>Whether the text is still arriving.</summary>
    public bool Streaming
    {
        get => this.streaming;
        set { this.streaming = value; if (this.binding is { } current) current.Streaming = value; }
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.binding = this.create(this.text, this.streaming);
        this.Content = this.binding.View;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        this.Content = null;
        this.binding?.Dispose();
        this.binding = null;
    }
}
