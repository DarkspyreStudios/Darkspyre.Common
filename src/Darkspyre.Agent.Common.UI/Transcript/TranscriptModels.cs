using System.Text.Json;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>What an attachment preview shows.</summary>
public enum PreviewState
{
    /// <summary>A lazily read image.</summary>
    ReadyImage,
    /// <summary>A document's kind icon.</summary>
    Document,
    /// <summary>An image whose preview is unavailable.</summary>
    Unavailable,
}

/// <summary>A retained or draft attachment's presentation, with an owner-bound opaque image key.</summary>
/// <param name="PreviewId">The actual preview identity.</param>
/// <param name="Position">The content or attachment position.</param>
/// <param name="Name">The attachment's display name.</param>
/// <param name="State">The presentation state.</param>
/// <param name="DocumentIcon">The document's Common icon.</param>
/// <param name="DocumentLabel">The accessible document kind.</param>
/// <param name="ImageKey">The immutable owner-bound read identity, or null when no image can be read.</param>
public sealed record TranscriptPreview(Guid PreviewId, int Position, string Name, PreviewState State,
    IconSource? DocumentIcon, string DocumentLabel, object? ImageKey);

/// <summary>A running tool's progress as shown on its card.</summary>
/// <param name="Value">The amount completed.</param>
/// <param name="Total">The total, when known.</param>
/// <param name="Message">The progress message.</param>
public sealed record TranscriptProgress(double Value, double? Total, string? Message);

/// <summary>The display fields of a tool call.</summary>
/// <param name="CallId">The actual invocation identity.</param>
/// <param name="Label">The owner's formatted call label.</param>
/// <param name="Arguments">The arguments as displayed.</param>
/// <param name="Running">Whether the call runs.</param>
/// <param name="Success">Whether the completed call succeeded, when known.</param>
/// <param name="Progress">The call's current progress.</param>
public sealed record TranscriptCall(string CallId, string Label, string Arguments,
    bool Running, bool? Success, TranscriptProgress? Progress);

/// <summary>The display fields shared by tool-content parts.</summary>
/// <param name="Position">The original content index.</param>
/// <param name="ForPerson">Whether this part is addressed to the person.</param>
/// <param name="Metadata">The original metadata, when present.</param>
/// <param name="OmittedBytes">The original byte count when content was not retained.</param>
public abstract record TranscriptPart(int Position, bool ForPerson, JsonElement? Metadata, long? OmittedBytes)
{
    /// <inheritdoc />
    public virtual bool Equals(TranscriptPart? other) => other is not null && this.EqualityContract == other.EqualityContract
        && this.Position == other.Position && this.ForPerson == other.ForPerson && this.OmittedBytes == other.OmittedBytes
        && TranscriptValues.Json(this.Metadata, other.Metadata);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(this.EqualityContract, this.Position, this.ForPerson, this.OmittedBytes);
}

/// <summary>A text content part.</summary>
/// <param name="Position">The original content index.</param>
/// <param name="ForPerson">Whether this part is addressed to the person.</param>
/// <param name="Metadata">The original metadata.</param>
/// <param name="OmittedBytes">The original byte count of omitted content.</param>
/// <param name="Text">The content text.</param>
public sealed record TranscriptTextPart(int Position, bool ForPerson, JsonElement? Metadata, long? OmittedBytes, string Text)
    : TranscriptPart(Position, ForPerson, Metadata, OmittedBytes)
{
    /// <inheritdoc />
    public bool Equals(TranscriptTextPart? other) => other is not null && base.Equals(other) && this.Text == other.Text;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), this.Text);
}

/// <summary>A resource's label, target and retained body.</summary>
/// <param name="Position">The original content index.</param>
/// <param name="ForPerson">Whether this part is addressed to the person.</param>
/// <param name="Metadata">The original metadata.</param>
/// <param name="OmittedBytes">The original byte count of omitted content.</param>
/// <param name="Label">The resource's display label.</param>
/// <param name="Uri">The resource target as displayed and copied.</param>
/// <param name="SuggestedName">The suggested name when saving its body.</param>
/// <param name="BodyText">The retained text body.</param>
/// <param name="BodyBytes">The retained binary body.</param>
public sealed record TranscriptResourcePart(int Position, bool ForPerson, JsonElement? Metadata, long? OmittedBytes,
    string Label, string Uri, string SuggestedName, string? BodyText, ReadOnlyMemory<byte>? BodyBytes)
    : TranscriptPart(Position, ForPerson, Metadata, OmittedBytes)
{
    /// <inheritdoc />
    public bool Equals(TranscriptResourcePart? other) => other is not null && base.Equals(other)
        && this.Label == other.Label && this.Uri == other.Uri && this.SuggestedName == other.SuggestedName
        && this.BodyText == other.BodyText && TranscriptValues.Bytes(this.BodyBytes, other.BodyBytes);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), this.Label, this.Uri, this.SuggestedName, this.BodyText);
}

/// <summary>A binary content part with the fields used for display and saving.</summary>
/// <param name="Position">The original content index.</param>
/// <param name="ForPerson">Whether this part is addressed to the person.</param>
/// <param name="Metadata">The original metadata.</param>
/// <param name="OmittedBytes">The original byte count of omitted content.</param>
/// <param name="Label">The file's display label.</param>
/// <param name="MediaType">The original media type.</param>
/// <param name="SuggestedName">The suggested save name.</param>
/// <param name="Bytes">The retained bytes, when available.</param>
public sealed record TranscriptBinaryPart(int Position, bool ForPerson, JsonElement? Metadata, long? OmittedBytes,
    string Label, string MediaType, string SuggestedName, ReadOnlyMemory<byte>? Bytes)
    : TranscriptPart(Position, ForPerson, Metadata, OmittedBytes)
{
    /// <inheritdoc />
    public bool Equals(TranscriptBinaryPart? other) => other is not null && base.Equals(other)
        && this.Label == other.Label && this.MediaType == other.MediaType && this.SuggestedName == other.SuggestedName
        && TranscriptValues.Bytes(this.Bytes, other.Bytes);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), this.Label, this.MediaType, this.SuggestedName);
}

/// <summary>The content and question display state of one tool result.</summary>
/// <param name="CallId">The actual call identity.</param>
/// <param name="Failed">Whether the tool reported failure.</param>
/// <param name="Parts">All original content parts in order.</param>
/// <param name="Structured">The structured result.</param>
/// <param name="Details">The retained details.</param>
/// <param name="WaitingForAnswer">Whether a human or peer question remains open.</param>
/// <param name="QuestionOutput">The owner's formatted question state or answer.</param>
public sealed record TranscriptOutput(string CallId, bool Failed, IReadOnlyList<TranscriptPart> Parts,
    JsonElement? Structured, JsonElement? Details, bool WaitingForAnswer, string? QuestionOutput)
{
    /// <inheritdoc />
    public bool Equals(TranscriptOutput? other) => other is not null && this.CallId == other.CallId && this.Failed == other.Failed
        && this.WaitingForAnswer == other.WaitingForAnswer && this.QuestionOutput == other.QuestionOutput
        && this.Parts.SequenceEqual(other.Parts) && TranscriptValues.Json(this.Structured, other.Structured)
        && TranscriptValues.Json(this.Details, other.Details);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(this.CallId, this.Failed, this.WaitingForAnswer, this.QuestionOutput, this.Parts.Count);
}

/// <summary>One owner-supplied existing text/PDF open action in a message row.</summary>
/// <param name="Name">The existing document name.</param>
/// <param name="Target">The opaque target passed back to the owner, never opened by the UI.</param>
/// <param name="Icon">The existing Common document icon.</param>
public sealed record TranscriptDocumentAction(string Name, string Target, IconSource Icon);

/// <summary>Only the message fields the actual transcript renderer consumes.</summary>
/// <param name="Role">The existing display role.</param>
/// <param name="Text">The complete display text.</param>
/// <param name="Id">The actual stored identity, or null for an unstored message.</param>
/// <param name="Sequence">The actual durable sequence, when present.</param>
/// <param name="ResponseId">The actual response identity.</param>
/// <param name="CanAskAgain">Whether the owner supports editing this user turn.</param>
/// <param name="Call">The call this message shows.</param>
/// <param name="Outputs">All results this message shows.</param>
/// <param name="Previews">The actual retained preview descriptors.</param>
/// <param name="ActivityTitle">The owner's formatted activity title.</param>
/// <param name="ActivityText">The owner's formatted activity text.</param>
public sealed record TranscriptMessage(string Role, string Text, Guid? Id, int? Sequence, Guid? ResponseId,
    bool CanAskAgain, TranscriptCall? Call, IReadOnlyList<TranscriptOutput> Outputs,
    IReadOnlyList<TranscriptPreview> Previews, string? ActivityTitle, string? ActivityText)
{
    /// <summary>Existing owner-supported text/PDF document actions; empty keeps the main row unchanged.</summary>
    public IReadOnlyList<TranscriptDocumentAction> Documents { get; init; } = [];

    /// <summary>The reasoning display role.</summary>
    public const string ReasoningRole = "reasoning";
    /// <summary>The context-compaction display role.</summary>
    public const string CompactionRole = "compaction";
    /// <summary>The user-command display role.</summary>
    public const string CommandRole = "command";
    /// <summary>The application-message display role.</summary>
    public const string ApplicationRole = "application";
    /// <summary>The failure-notice display role.</summary>
    public const string NoticeRole = "notice";
    /// <summary>The worker-response display role.</summary>
    public const string WorkerRole = "worker";
    /// <summary>The human-answer display role, rendered on its asking card.</summary>
    public const string AnswerRole = "answer";

    /// <inheritdoc />
    public bool Equals(TranscriptMessage? other) => other is not null && this.Role == other.Role && this.Text == other.Text
        && this.Id == other.Id && this.Sequence == other.Sequence && this.ResponseId == other.ResponseId
        && this.CanAskAgain == other.CanAskAgain && Equals(this.Call, other.Call)
        && this.ActivityTitle == other.ActivityTitle && this.ActivityText == other.ActivityText
        && this.Outputs.SequenceEqual(other.Outputs) && this.Previews.SequenceEqual(other.Previews)
        && this.Documents.SequenceEqual(other.Documents);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Role);
        hash.Add(this.Text);
        hash.Add(this.Id);
        hash.Add(this.Sequence);
        hash.Add(this.ResponseId);
        hash.Add(this.CanAskAgain);
        hash.Add(this.Call);
        hash.Add(this.ActivityTitle);
        hash.Add(this.ActivityText);
        hash.Add(this.Outputs.Count);
        hash.Add(this.Previews.Count);
        hash.Add(this.Documents.Count);
        return hash.ToHashCode();
    }
}

/// <summary>A queued message's text and ordered attachment previews.</summary>
/// <param name="PromptId">The actual pending prompt identity.</param>
/// <param name="Text">The current text.</param>
/// <param name="Previews">The ordered preview descriptors.</param>
public sealed record TranscriptWaiting(Guid PromptId, string Text, IReadOnlyList<TranscriptPreview> Previews)
{
    /// <inheritdoc />
    public bool Equals(TranscriptWaiting? other) => other is not null && this.PromptId == other.PromptId
        && this.Text == other.Text && this.Previews.SequenceEqual(other.Previews);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(this.PromptId, this.Text, this.Previews.Count);
}

// Content equality crosses deserialization boundaries; JSON and bytes cannot use reference equality.
internal static class TranscriptValues
{
    internal static bool Json(JsonElement? left, JsonElement? right) => left.HasValue == right.HasValue
        && (!left.HasValue || left.Value.ValueKind == right!.Value.ValueKind
            && (left.Value.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(left.Value, right.Value)));

    internal static bool Bytes(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right) => left.HasValue == right.HasValue
        && (!left.HasValue || left.Value.Span.SequenceEqual(right!.Value.Span));
}
