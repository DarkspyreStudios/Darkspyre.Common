using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// The account quota gauge. The ring fills as the most used allowance is used, and its level moves its colour from the
/// theme's ok colour through warn to danger. With nothing to measure it is a grey ring with a question mark.
/// </summary>
public sealed class QuotaRing : Control
{
    private double? used;
    private bool stale;
    private bool updating;

    /// <summary>Creates the 14 px ring.</summary>
    public QuotaRing()
    {
        this.Width = 14;
        this.Height = 14;
        this.Classes.Add("quota-ring");
        // A theme change redraws the ring in the new colours.
        this.ResourcesChanged += (_, _) => this.InvalidateVisual();
    }

    /// <summary>The fill level from zero through ten for <paramref name="used"/>.</summary>
    internal static int Level(double used) => (int)Math.Round(Math.Clamp(used, 0, 1) * 10, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Draws <paramref name="fraction"/> of the allowance used, null when unknown. A <paramref name="isStale"/> reading
    /// keeps its fill, dimmed; while <paramref name="isUpdating"/> the ring keeps what it last drew and pulses.
    /// </summary>
    public void Show(double? fraction, bool isStale, bool isUpdating)
    {
        this.used = fraction;
        this.stale = isStale;
        this.updating = isUpdating;
        this.Classes.Set("quota-unknown", fraction is null);
        this.Classes.Set("quota-stale", isStale && fraction is not null);
        this.Classes.Set("quota-updating", isUpdating);
        for (int level = 0; level <= 10; level++) this.Classes.Set($"quota-level-{level}", fraction is { } value && Level(value) == level);
        this.InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Point centre = new(this.Bounds.Width / 2, this.Bounds.Height / 2);
        double radius = Math.Min(this.Bounds.Width, this.Bounds.Height) / 2 - 1.5;
        using DrawingContext.PushedState faded = context.PushOpacity(this.stale && this.used is not null ? 0.45 : 1);
        IBrush track = this.Resource(this.used is null ? "faint" : "line") is { } line ? new SolidColorBrush(line) : Brushes.Gray;
        context.DrawEllipse(null, new Pen(track, 2), centre, radius, radius);
        if (this.used is { } fraction)
        {
            IBrush fill = new SolidColorBrush(this.LevelColour(Level(fraction)));
            double sweep = Math.Clamp(fraction, 0, 1) * 360;
            if (sweep >= 359.9) context.DrawEllipse(null, new Pen(fill, 2), centre, radius, radius);
            else if (sweep > 0)
            {
                StreamGeometry arc = new();
                using (StreamGeometryContext path = arc.Open())
                {
                    Point start = new(centre.X, centre.Y - radius);
                    double end = (sweep - 90) * Math.PI / 180;
                    path.BeginFigure(start, false);
                    path.ArcTo(new Point(centre.X + radius * Math.Cos(end), centre.Y + radius * Math.Sin(end)), new Size(radius, radius), 0,
                        sweep > 180, SweepDirection.Clockwise);
                    path.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(fill, 2, lineCap: PenLineCap.Round), arc);
            }
        }
        else
        {
            FormattedText mark = new("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, track);
            context.DrawText(mark, new Point(centre.X - mark.Width / 2, centre.Y - mark.Height / 2));
        }
    }

    // Level 0 is the ok colour and level 10 the danger colour; each level between moves a fifth of the way from ok to
    // warn, then from warn to danger.
    private Color LevelColour(int level)
    {
        Color ok = this.Resource("ok") ?? Colors.Green;
        Color warn = this.Resource("warn") ?? Colors.Orange;
        Color danger = this.Resource("danger") ?? Colors.Red;
        return level <= 5 ? Mix(ok, warn, level / 5.0) : Mix(warn, danger, (level - 5) / 5.0);
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * amount), (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private Color? Resource(string key) => this.TryFindResource(key, this.ActualThemeVariant, out object? value) && value is Color color ? color : null;
}
