using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>A turning three-quarter ring that shows work in progress, in the current text colour.</summary>
public sealed class Spinner : Decorator
{
    private static readonly Animation Turn = new()
    {
        Duration = TimeSpan.FromSeconds(1),
        IterationCount = IterationCount.Infinite,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
        },
    };

    /// <summary>Creates a spinner <paramref name="size"/> DIP across; the extra-small size is 12.</summary>
    public Spinner(double size = 12)
    {
        this.Width = size;
        this.Height = size;
        Arc arc = new()
        {
            StartAngle = 0,
            SweepAngle = 270,
            StrokeThickness = Math.Max(1.5, size / 8),
            StrokeLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(),
        };
        arc.Bind(Shape.StrokeProperty, this.GetObservable(TextElement.ForegroundProperty));
        LoopingAnimation.Play(arc, Turn);
        this.Child = arc;
        this.Classes.Add("spinner");
    }
}
