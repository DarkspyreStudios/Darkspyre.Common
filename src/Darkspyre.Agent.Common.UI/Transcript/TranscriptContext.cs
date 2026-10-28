using Avalonia.Controls;
using Darkspyre.Common.Markdown;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>The outcome of asking the owner to begin editing a queued prompt.</summary>
public enum QueueBeginStatus
{
    /// <summary>The owner holds the prompt for this editor.</summary>
    Held,
    /// <summary>The prompt no longer waits.</summary>
    NotWaiting,
    /// <summary>Another editor holds the prompt.</summary>
    EditedElsewhere,
}

/// <summary>The outcome of asking the owner to save a queued edit.</summary>
public enum QueueSaveStatus
{
    /// <summary>The edit was saved.</summary>
    Saved,
    /// <summary>The prompt no longer waits.</summary>
    NotWaiting,
    /// <summary>The editor's hold ended.</summary>
    HoldEnded,
}

/// <summary>An owner's begin-edit result with its opaque hold handle.</summary>
/// <param name="Status">Whether the hold was granted.</param>
/// <param name="Handle">The owner's handle; a granted Remote hold may have a null handle.</param>
/// <gotcha>Status, not a non-null handle, determines whether editing can begin.</gotcha>
public sealed record QueueBegin(QueueBeginStatus Status, object? Handle);

/// <summary>The existing operations one transcript's owner supports.</summary>
/// <param name="Edit">Edits a stored prompt through its owner.</param>
/// <param name="Steer">Steers an eligible queued prompt.</param>
/// <param name="Remove">Removes a queued prompt with the owner's existing confirmation.</param>
/// <param name="BeginEdit">Begins a queued edit.</param>
/// <param name="SaveEdit">Saves text and the kept attachment positions.</param>
/// <param name="CancelEdit">Cancels a queued edit.</param>
/// <param name="ConfirmDiscard">The owner's existing dirty-cancel guard; null adds no guard.</param>
public sealed record TranscriptActions(Func<Guid, Task>? Edit, Func<Guid, Task>? Steer,
    Func<Guid, Task>? Remove, Func<Guid, Task<QueueBegin>>? BeginEdit,
    Func<Guid, object?, string, IReadOnlyList<int>, Task<QueueSaveStatus>>? SaveEdit,
    Func<Guid, object?, Task>? CancelEdit, Func<bool, Task<bool>>? ConfirmDiscard = null);

/// <summary>One replaceable Markdown text binding supplied by the transcript's owner.</summary>
public interface ITranscriptMarkdown : IDisposable
{
    /// <summary>The binding's control, which hosts its current Markdown plugin view.</summary>
    Control View { get; }
    /// <summary>The complete Markdown text.</summary>
    string Text { get; set; }
    /// <summary>Whether the text is still streaming.</summary>
    bool Streaming { get; set; }
}

/// <summary>The presentation collaborators of one transcript, with all owner operations kept outside the UI.</summary>
/// <param name="Markdown">Creates the specific Markdown plugin binding, not an arbitrary row control.</param>
/// <param name="Images">The pane's lazy image leases.</param>
/// <param name="Links">The owner's file-link source, when available.</param>
/// <param name="OpenLink">Opens a target through the captured owner, never the controller's filesystem.</param>
/// <param name="Save">Saves retained content through the owner's existing picker and file permission path.</param>
/// <param name="Warn">Reports an owner-operation failure.</param>
public sealed record TranscriptContext(Func<string, bool, ITranscriptMarkdown> Markdown,
    AttachmentPreviewImages Images, IMarkdownLinkSource? Links, Func<string, Control, Task> OpenLink,
    Func<string, ReadOnlyMemory<byte>, Task> Save, Func<string, Task> Warn);
