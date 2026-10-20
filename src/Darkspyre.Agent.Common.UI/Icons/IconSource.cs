namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>
/// One piece of Darkspyre artwork: SVG elements in a view box. <c>currentColor</c> takes the foreground of the
/// <see cref="Icon"/> that draws it.
/// </summary>
/// <param name="Markup">The SVG elements, without the enclosing <c>svg</c> element.</param>
/// <param name="ViewBox">The SVG view box: minimum x, minimum y, width and height.</param>
public sealed record IconSource(string Markup, string ViewBox)
{
    /// <summary>Artwork drawn from <paramref name="markup"/> in <paramref name="viewBox"/>.</summary>
    public static IconSource Fill(string markup, string viewBox) => new(markup, viewBox);
}
