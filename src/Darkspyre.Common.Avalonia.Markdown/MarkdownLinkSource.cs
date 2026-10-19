namespace Darkspyre.Common.Avalonia.Markdown;

/// <summary>Finds links in text a MarkdownViewer shows. Called while the viewer parses, possibly off the UI thread.</summary>
/// <remarks>
/// The viewer calls <see cref="Find"/> on plain text outside links, and <see cref="Accepts"/> on each Markdown link's
/// own target and on each code span's trimmed text that is not a web or mail address. A click on a link whose target
/// is not a web or mail address raises <see cref="MarkdownViewer.LinkActivated"/> only when <see cref="Accepts"/> is
/// true for that target.
/// </remarks>
/// <gotcha>A target <see cref="Find"/> returns must also be accepted by <see cref="Accepts"/>, or a click on its link
/// does nothing.</gotcha>
public interface IMarkdownLinkSource
{
    /// <summary>The link spans in text, in order, without overlaps.</summary>
    IReadOnlyList<MarkdownLinkSpan> Find(string text);

    /// <summary>Whether a Markdown link's own non-web target is a link this source opens.</summary>
    bool Accepts(string target);

    /// <summary>Raised when the source can link more than before.</summary>
    event EventHandler? Changed;
}

/// <summary>One link span: its start and length in the text and the target the viewer activates.</summary>
/// <param name="Start">The span's first character in the text.</param>
/// <param name="Length">The span's length in characters.</param>
/// <param name="Target">The target the link activates.</param>
public readonly record struct MarkdownLinkSpan(int Start, int Length, string Target);
