using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// The highlight a scope draws over a plain <see cref="TextBlock"/> with a selected range: the range's rectangles in the
/// theme's <c>color-primary.brush.40</c>, the brush selectable text blocks highlight with.
/// </summary>
internal sealed class TextHighlight : Control
{
    private static readonly IBrush Fallback = new SolidColorBrush(Colors.DodgerBlue, 0.4);

    private readonly TextBlock block;

    private TextHighlight(TextBlock block)
    {
        this.block = block;
        this.IsHitTestVisible = false;
    }

    /// <summary>The first highlighted character.</summary>
    public int Start { get; private set; }

    /// <summary>The character after the last highlighted one.</summary>
    public int End { get; private set; }

    /// <summary>Highlights <paramref name="start"/> to <paramref name="end"/> of <paramref name="block"/>, adding its
    /// adorner when it has none; an empty range removes it.</summary>
    public static void Show(TextBlock block, int start, int end)
    {
        TextHighlight? shown = AdornerLayer.GetAdorner(block) as TextHighlight;
        if (end <= start)
        {
            if (shown is not null) AdornerLayer.SetAdorner(block, null);
            return;
        }
        if (shown is null)
        {
            shown = new TextHighlight(block);
            AdornerLayer.SetAdorner(block, shown);
        }
        if (shown.Start == start && shown.End == end) return;
        shown.Start = start;
        shown.End = end;
        shown.InvalidateVisual();
    }

    /// <summary>The theme's highlight brush, the one selectable text blocks use.</summary>
    public IBrush Brush => this.block.TryFindResource("color-primary.brush.40", this.block.ActualThemeVariant, out object? found) && found is IBrush themed
        ? themed : Fallback;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        IBrush brush = this.Brush;
        Thickness padding = this.block.Padding;
        foreach (Rect rect in this.block.TextLayout.HitTestTextRange(this.Start, this.End - this.Start))
            context.FillRectangle(brush, rect.Translate(new Vector(padding.Left, padding.Top)));
    }
}
