namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>A derived rendering of the shared transcript, never another history owner.</summary>
internal static class TranscriptLayout
{
    internal sealed record Row(object Key, TranscriptMessage? Message, List<TranscriptMessage>? Activity)
    {
        internal bool Live { get; init; }

        /// <summary>The absolute position of the row's first message.</summary>
        internal int Position { get; init; }
    }

    /// <summary>
    /// Rows in transcript order. Consecutive reasoning, tool calls and tool output form one activity row, so
    /// each stretch of work between replies collapses to one line. Output addressed to the person is also
    /// shown as its own row. Reasoning still arriving joins the last activity row, or starts one. A message with no
    /// text has no row unless it is a reply or prompt that holds attachment previews.
    /// Row keys hold absolute turn positions: <paramref name="offset"/> is how many stored turns come before
    /// <paramref name="messages"/>, so a key stays the same when older turns are loaded in front.
    /// </summary>
    internal static IReadOnlyList<Row> Build(IReadOnlyList<TranscriptMessage> messages, string liveReasoning = "", bool busy = false, int offset = 0)
    {
        List<Row> rows = [];
        List<TranscriptMessage>? activity = null;
        IEnumerable<TranscriptMessage> shown = liveReasoning.Length == 0 ? messages
            : messages.Append(new TranscriptMessage(TranscriptMessage.ReasoningRole, liveReasoning, null, null, null, false, null, [], [], null, null));
        int index = offset - 1;
        foreach (TranscriptMessage message in shown)
        {
            index = message.Sequence is { } sequence ? sequence : index + 1;
            // A human answer that opened a turn shows on the asking call's card, not as a row of its own.
            if (message.Role == TranscriptMessage.AnswerRole) continue;
            // A response or tool result with no text keeps its position but has no row. A prompt or response with no
            // text that holds attachment previews still has a row, which shows them.
            if (message.Text.Length == 0 && (message.Previews.Count == 0 || IsActivity(message))) continue;
            if (!IsActivity(message))
            {
                activity = null;
                rows.Add(new(("message", index), message, null) { Position = index });
                continue;
            }

            if (message.Role != "tool" || HasActivity(message))
            {
                if (activity is null) rows.Add(new(("activity", index), null, activity = []) { Position = index });
                activity.Add(message);
            }
            if (message.Outputs.Any(output => output.Parts.Any(part => part.ForPerson)))
                rows.Add(new(("display", index), message, null) { Position = index });
        }
        if (busy)
        {
            // Keep a status line even before the provider emits its first activity or after an
            // intermediate reply. Direct user-facing tool output does not end its activity group.
            int active = activity is null ? -1 : rows.FindLastIndex(row => ReferenceEquals(row.Activity, activity));
            if (active < 0) rows.Add(new(("activity", index + 1), null, []) { Live = true, Position = index + 1 });
            else rows[active] = rows[active] with { Live = true };
        }
        return rows;
    }

    /// <summary>
    /// Work toward a reply rather than a reply: every turn except the user's prompt, a composer command the user ran and
    /// the model's response, such as reasoning, a tool call, tool output, a context compaction, a turn the application sent
    /// to the model, a background worker's response or a failure notice.
    /// </summary>
    internal static bool IsActivity(TranscriptMessage message) => message.Role is not ("user" or "assistant" or TranscriptMessage.CommandRole);

    internal static bool HasActivity(TranscriptMessage message) => message.Outputs.Count == 0
        || message.Outputs.Any(output => output.Parts.Count == 0 || output.Parts.Any(part => !part.ForPerson));
}
