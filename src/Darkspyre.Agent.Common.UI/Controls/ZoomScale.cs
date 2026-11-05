namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// An in-memory zoom scale factor bounded between 1.0 (100%) and 3.0 (300%).
/// </summary>
public sealed class ZoomScale
{
    /// <summary>The minimum allowed zoom scale factor.</summary>
    public const double Minimum = 1.0;

    /// <summary>The maximum allowed zoom scale factor.</summary>
    public const double Maximum = 3.0;

    /// <summary>The zoom increment per wheel notch.</summary>
    public const double Step = 0.1;

    private double value;

    /// <summary>Initializes a new instance of the <see cref="ZoomScale"/> class.</summary>
    /// <param name="initial">The initial zoom scale factor, clamped between <see cref="Minimum"/> and <see cref="Maximum"/>.</param>
    public ZoomScale(double initial = Minimum)
    {
        this.value = Math.Clamp(Math.Round(initial, 4), Minimum, Maximum);
    }

    /// <summary>Occurs when the zoom scale factor actually changes.</summary>
    public event Action? Changed;

    /// <summary>Gets or sets the zoom scale factor, clamped between <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
    public double Value
    {
        get => this.value;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 4), Minimum, Maximum);
            if (Math.Abs(this.value - clamped) < 0.0001)
            {
                return;
            }

            this.value = clamped;
            this.Changed?.Invoke();
        }
    }
}
