namespace Darkspyre.Common.Avalonia.Pdf;

/// <summary>How large the viewer draws pages.</summary>
public abstract record PdfZoom
{
    /// <summary>The percentages the zoom list offers and zoom in and out step through.</summary>
    public static IReadOnlyList<int> Percents { get; } = [50, 75, 100, 125, 150, 200, 400];

    private PdfZoom()
    {
    }

    /// <summary>A fixed size: 100 is the page's physical size.</summary>
    /// <param name="Value">The percentage, from 50 to 400.</param>
    public sealed record Percent(int Value) : PdfZoom;

    /// <summary>The widest page fills the view's width.</summary>
    public sealed record FitWidth : PdfZoom;

    /// <summary>The current page fits entirely in the view.</summary>
    public sealed record FitPage : PdfZoom;
}
