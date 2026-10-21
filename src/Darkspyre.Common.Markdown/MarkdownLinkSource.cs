namespace Darkspyre.Common.Markdown;

/// <summary>Finds links in text a Markdown viewer shows. Called while the viewer parses, possibly off the UI thread.</summary>
/// <remarks>
/// A viewer whose pipeline <see cref="MarkdownSafety.CreateBuilder"/> built with this source calls <see cref="Accepts"/>
/// on each Markdown link's own target that is not a web or mail address; a refused link shows as its own text. The
/// desktop viewer also calls <see cref="Find"/> on plain text outside links and <see cref="Accepts"/> on each code span's
/// trimmed text that is not a web or mail address, and raises its link event for such a target only when
/// <see cref="Accepts"/> is true for it.
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
