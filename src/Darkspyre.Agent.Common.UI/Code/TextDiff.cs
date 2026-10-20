namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>How a line ends.</summary>
public enum LineEnding
{
    /// <summary>The last line of a text that does not end with a line break.</summary>
    None,
    /// <summary>A line feed.</summary>
    Lf,
    /// <summary>A carriage return and a line feed.</summary>
    CrLf,
    /// <summary>A carriage return.</summary>
    Cr,
}

/// <summary>One line of text without its line break, and the break that ends it.</summary>
/// <param name="Text">The line's text.</param>
/// <param name="Ending">The line break that ends the line.</param>
public sealed record TextLine(string Text, LineEnding Ending)
{
    /// <summary>Splits <paramref name="text"/> into lines. A final line break does not start an empty line.</summary>
    public static IReadOnlyList<TextLine> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        List<TextLine> lines = [];
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            int end = i;
            LineEnding ending = text[i] == '\n' ? LineEnding.Lf : LineEnding.Cr;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                ending = LineEnding.CrLf;
                i++;
            }
            lines.Add(new(text[start..end], ending));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(new(text[start..], LineEnding.None));
        return lines.AsReadOnly();
    }
}

/// <summary>Whether a diff line is unchanged, added or removed.</summary>
public enum DiffLineKind
{
    /// <summary>The line is on both sides.</summary>
    Context,
    /// <summary>The line is only on the new side.</summary>
    Add,
    /// <summary>The line is only on the old side.</summary>
    Remove,
}

/// <summary>One line of a diff.</summary>
/// <param name="Kind">Whether the line is unchanged, added or removed.</param>
/// <param name="Text">The line's text.</param>
/// <param name="Ending">The line break that ends it.</param>
/// <param name="OldLine">Its one-based line number on the old side, or null for an added line.</param>
/// <param name="NewLine">Its one-based line number on the new side, or null for a removed line.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, LineEnding Ending, int? OldLine, int? NewLine);

/// <summary>A run of changes with up to three unchanged lines around it, in unified diff terms.</summary>
/// <param name="OldStartLine">The first old line the hunk covers.</param>
/// <param name="OldCount">How many old lines it covers.</param>
/// <param name="NewStartLine">The first new line the hunk covers.</param>
/// <param name="NewCount">How many new lines it covers.</param>
/// <param name="Lines">Its lines in order.</param>
public sealed record DiffHunk(int OldStartLine, int OldCount, int NewStartLine, int NewCount, IReadOnlyList<DiffLine> Lines);

/// <summary>Bounded line diff. Line breaks take part in equality, so a changed line ending is a changed line.</summary>
public static class TextDiff
{
    // The longest-common-subsequence table holds one cell per pair of changed lines; 4 million cells is 16 MB.
    private const long MaximumCells = 4_000_000;

    /// <summary>The hunks that turn <paramref name="before"/> into <paramref name="after"/>.</summary>
    /// <exception cref="IOException">The changed region is larger than the diff's work limit.</exception>
    public static IReadOnlyList<DiffHunk> Compute(IReadOnlyList<TextLine> before, IReadOnlyList<TextLine> after, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        IReadOnlyList<TextLine> oldLines = before;
        IReadOnlyList<TextLine> newLines = after;
        int prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
        {
            cancellationToken.ThrowIfCancellationRequested();
            prefix++;
        }
        int suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix
            && oldLines[oldLines.Count - suffix - 1] == newLines[newLines.Count - suffix - 1])
        {
            cancellationToken.ThrowIfCancellationRequested();
            suffix++;
        }
        int n = oldLines.Count - prefix - suffix, m = newLines.Count - prefix - suffix;
        if (n == 0 && m == 0) return [];
        if ((long)(n + 1) * (m + 1) > MaximumCells && n != 0 && m != 0)
            throw new IOException("Comparison exceeds the text-diff work limit.");
        List<DiffLine> edits = [];
        int oldPosition = prefix + 1, newPosition = prefix + 1;
        void Add(DiffLineKind kind, TextLine line) =>
            edits.Add(new(kind, line.Text, line.Ending, kind == DiffLineKind.Add ? null : oldPosition++, kind == DiffLineKind.Remove ? null : newPosition++));
        for (int i = Math.Max(0, prefix - 3); i < prefix; i++)
            edits.Add(new(DiffLineKind.Context, oldLines[i].Text, oldLines[i].Ending, i + 1, i + 1));
        if (n == 0)
        {
            for (int j = 0; j < m; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(DiffLineKind.Add, newLines[prefix + j]);
            }
        }
        else if (m == 0)
        {
            for (int i = 0; i < n; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(DiffLineKind.Remove, oldLines[prefix + i]);
            }
        }
        else
        {
            int[,] lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = oldLines[prefix + i] == newLines[prefix + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (x < n && y < m && oldLines[prefix + x] == newLines[prefix + y])
                {
                    Add(DiffLineKind.Context, oldLines[prefix + x++]);
                    y++;
                }
                else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1])) Add(DiffLineKind.Remove, oldLines[prefix + x++]);
                else Add(DiffLineKind.Add, newLines[prefix + y++]);
            }
        }
        for (int i = 0; i < Math.Min(3, suffix); i++) Add(DiffLineKind.Context, oldLines[oldLines.Count - suffix + i]);
        List<DiffHunk> hunks = [];
        int start = 0;
        while (start < edits.Count)
        {
            int first = edits.FindIndex(start, line => line.Kind != DiffLineKind.Context);
            if (first < 0) break;
            int end = first + 1, lastChange = first;
            while (end < edits.Count)
            {
                if (edits[end].Kind != DiffLineKind.Context)
                {
                    // More than six unchanged lines between two changes starts a new hunk.
                    if (end - lastChange > 7) break;
                    lastChange = end;
                }
                end++;
            }
            int from = Math.Max(start, first - 3), until = Math.Min(edits.Count, lastChange + 4);
            DiffLine[] lines = [.. edits.GetRange(from, until - from)];
            int oldStart = lines.FirstOrDefault(line => line.OldLine.HasValue)?.OldLine
                ?? (from > 0 ? edits.Take(from).LastOrDefault(line => line.OldLine.HasValue)?.OldLine + 1 : prefix + 1) ?? 1;
            int newStart = lines.FirstOrDefault(line => line.NewLine.HasValue)?.NewLine
                ?? (from > 0 ? edits.Take(from).LastOrDefault(line => line.NewLine.HasValue)?.NewLine + 1 : prefix + 1) ?? 1;
            hunks.Add(new(oldStart, lines.Count(line => line.Kind != DiffLineKind.Add), newStart,
                lines.Count(line => line.Kind != DiffLineKind.Remove), Array.AsReadOnly(lines)));
            start = until;
        }
        return hunks.AsReadOnly();
    }
}
