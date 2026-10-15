using System.Globalization;
using Avalonia.Media;
using Mermaider;
using Mermaider.Models;
using RenderOptions = Mermaider.Models.RenderOptions;

namespace Darkspyre.Common.Avalonia.Markdown;

/// <summary>Turns Mermaid source into SVG. Returns null when the source cannot be drawn; the fence then stays a code block.</summary>
public interface IMermaidRenderer
{
    /// <summary>The SVG for <paramref name="source"/> drawn in <paramref name="theme"/>'s colours, or null when it cannot be drawn.</summary>
    ValueTask<string?> RenderSvgAsync(string source, MermaidTheme theme, CancellationToken cancellationToken);
}

/// <summary>Colours a diagram is drawn with, taken from the current theme tokens.</summary>
/// <param name="Background">Behind the diagram: the panel colour.</param>
/// <param name="Foreground">Text: the text colour.</param>
/// <param name="Accent">Arrow heads and highlights: the accent colour.</param>
/// <param name="Border">Box outlines and lines: the line colour.</param>
/// <param name="FontFamily">The family all diagram text uses.</param>
public sealed record MermaidTheme(Color Background, Color Foreground, Color Accent, Color Border, string FontFamily);

/// <summary>
/// Default renderer: Mermaider draws the diagram, and the flattener resolves the CSS custom properties and colour mixes
/// in its SVG so a renderer without CSS, such as Svg.Skia, draws every element.
/// </summary>
public sealed class MermaiderRenderer : IMermaidRenderer
{
    /// <inheritdoc />
    public async ValueTask<string?> RenderSvgAsync(string source, MermaidTheme theme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrWhiteSpace(source)) return null;
        RenderOptions options = new()
        {
            Bg = Hex(theme.Background),
            Fg = Hex(theme.Foreground),
            Accent = Hex(theme.Accent),
            Border = Hex(theme.Border),
            Font = theme.FontFamily,
            Transparent = true,
            Gradient = false,
            // A drop shadow is decoration that Svg.Skia does not draw; the flattener strips any that remain.
            Elevation = 0,
        };
        try
        {
            using MemoryStream output = new();
            await MermaidRenderer.RenderSvgAsync(source, output, options, cancellationToken).ConfigureAwait(false);
            string svg = System.Text.Encoding.UTF8.GetString(output.ToArray());
            return string.IsNullOrWhiteSpace(svg) || svg == MermaidRenderer.FallbackSvg ? null : SvgCssFlattener.Flatten(svg);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Mermaider reports source it cannot parse or lay out with its own exception types; any of them leaves the fence as code.
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static string Hex(Color colour) => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}");
}
