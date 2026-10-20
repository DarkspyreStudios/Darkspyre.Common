using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>Draws an <see cref="IconSource"/> scaled to the control's size, in the control's foreground.</summary>
/// <remarks>
/// It reads the SVG elements the application's artwork uses: <c>path</c>, <c>rect</c>, <c>circle</c>, <c>text</c> and an
/// <c>image</c> from the application's assets, with fill, fill rule, stroke, stroke width, line caps and joins.
/// </remarks>
public sealed class Icon : Control
{
    /// <summary>Defines the <see cref="Source"/> property.</summary>
    public static readonly StyledProperty<IconSource?> SourceProperty = AvaloniaProperty.Register<Icon, IconSource?>(nameof(Source));

    /// <summary>Defines the <see cref="Foreground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<Icon>();

    private Viewbox? drawing;

    static Icon()
    {
        AffectsMeasure<Icon>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.Rebuild());
        ForegroundProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.Rebuild());
    }

    /// <summary>Creates an icon 16 device-independent pixels square, the size the artwork is drawn for.</summary>
    public Icon()
    {
        this.Width = 16;
        this.Height = 16;
    }

    /// <summary>The artwork to draw.</summary>
    public IconSource? Source
    {
        get => this.GetValue(SourceProperty);
        set => this.SetValue(SourceProperty, value);
    }

    /// <summary>The colour <c>currentColor</c> stands for; inherited from the surrounding text.</summary>
    public IBrush? Foreground
    {
        get => this.GetValue(ForegroundProperty);
        set => this.SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        this.drawing?.Measure(availableSize);
        return this.drawing?.DesiredSize ?? default;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        this.drawing?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void Rebuild()
    {
        if (this.drawing is not null)
        {
            this.VisualChildren.Remove(this.drawing);
            this.LogicalChildren.Remove(this.drawing);
            this.drawing = null;
        }
        if (this.Source is not { } source) return;
        this.drawing = Build(source, this.Foreground ?? Brushes.Black);
        this.LogicalChildren.Add(this.drawing);
        this.VisualChildren.Add(this.drawing);
        this.InvalidateMeasure();
    }

    private static Viewbox Build(IconSource source, IBrush current)
    {
        double[] box = [.. source.ViewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number)];
        Canvas canvas = new() { Width = box[2], Height = box[3] };
        XElement root = XElement.Parse("<svg>" + source.Markup + "</svg>");
        foreach (XElement element in root.Elements())
            if (Shape(element, current) is { } drawn)
            {
                // An element with no position of its own reads NaN, which is 0 in SVG.
                Canvas.SetLeft(drawn, Position(Canvas.GetLeft(drawn)) - box[0]);
                Canvas.SetTop(drawn, Position(Canvas.GetTop(drawn)) - box[1]);
                canvas.Children.Add(drawn);
            }
        return new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    private static double Position(double value) => double.IsNaN(value) ? 0 : value;

    private static Control? Shape(XElement element, IBrush current)
    {
        switch (element.Name.LocalName)
        {
            case "path":
            {
                string d = (string)element.Attribute("d")!;
                // A named SVG fill-rule applies; artwork that names none keeps Avalonia's default, which the application's
                // own icons are drawn for.
                Geometry data = (string?)element.Attribute("fill-rule") switch
                {
                    "evenodd" => WithFillRule(d, FillRule.EvenOdd),
                    "nonzero" => WithFillRule(d, FillRule.NonZero),
                    _ => Geometry.Parse(d),
                };
                return Styled(new Avalonia.Controls.Shapes.Path { Data = data }, element, current);
            }
            case "rect":
            {
                Rectangle rect = Styled(new Rectangle
                {
                    Width = Number(element, "width"),
                    Height = Number(element, "height"),
                    RadiusX = Number(element, "rx"),
                    RadiusY = element.Attribute("ry") is null ? Number(element, "rx") : Number(element, "ry"),
                }, element, current);
                Canvas.SetLeft(rect, Number(element, "x"));
                Canvas.SetTop(rect, Number(element, "y"));
                return rect;
            }
            case "circle":
            {
                double r = Number(element, "r");
                Ellipse circle = Styled(new Ellipse { Width = r * 2, Height = r * 2 }, element, current);
                Canvas.SetLeft(circle, Number(element, "cx") - r);
                Canvas.SetTop(circle, Number(element, "cy") - r);
                return circle;
            }
            case "text":
            {
                double size = Number(element, "font-size");
                TextBlock text = new()
                {
                    Text = element.Value,
                    FontSize = size,
                    FontWeight = element.Attribute("font-weight") is { } weight ? (FontWeight)int.Parse(weight.Value, CultureInfo.InvariantCulture) : FontWeight.Normal,
                    Foreground = Paint((string?)element.Attribute("fill"), current) ?? current,
                };
                text.Measure(Size.Infinity);
                double x = Number(element, "x");
                if ((string?)element.Attribute("text-anchor") == "middle") x -= text.DesiredSize.Width / 2;
                Canvas.SetLeft(text, x);
                // SVG places text by its baseline; the line height's top sits about one font size above it.
                Canvas.SetTop(text, Number(element, "y") - size);
                return text;
            }
            case "image":
            {
                string href = (string)element.Attribute("href")!;
                Image image = new()
                {
                    Source = new Bitmap(AssetLoader.Open(new Uri("avares://Darkspyre.Agent.Common.UI/Assets/" + System.IO.Path.GetFileName(href)))),
                    Width = Number(element, "width"),
                    Height = Number(element, "height"),
                };
                return image;
            }
            default:
                return null;
        }
    }

    private static PathGeometry WithFillRule(string data, FillRule rule)
    {
        PathGeometry geometry = PathGeometry.Parse(data);
        geometry.FillRule = rule;
        return geometry;
    }

    private static T Styled<T>(T shape, XElement element, IBrush current) where T : Avalonia.Controls.Shapes.Shape
    {
        shape.Fill = Paint((string?)element.Attribute("fill"), current, defaultFill: true);
        shape.Stroke = Paint((string?)element.Attribute("stroke"), current);
        shape.StrokeThickness = element.Attribute("stroke-width") is null ? 1 : Number(element, "stroke-width");
        shape.StrokeLineCap = (string?)element.Attribute("stroke-linecap") switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat,
        };
        shape.StrokeJoin = (string?)element.Attribute("stroke-linejoin") switch
        {
            "round" => PenLineJoin.Round,
            "bevel" => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter,
        };
        return shape;
    }

    // SVG fills a shape black when it names no fill; the artwork always names one or none.
    private static IBrush? Paint(string? value, IBrush current, bool defaultFill = false) => value switch
    {
        null => defaultFill ? current : null,
        "none" => null,
        "currentColor" => current,
        _ => Brush.Parse(value),
    };

    private static double Number(XElement element, string name) =>
        element.Attribute(name) is { } value ? Number(value.Value) : 0;

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}
