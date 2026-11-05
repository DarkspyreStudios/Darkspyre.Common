namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// An in-memory zoom scale factor bounded between 1.0 (100%) and 3.0 (300%).
/// </summary>
public sealed class ZoomScale
{
    private double value = 1.0;

    /// <summary>Initializes a new instance of the <see cref="ZoomScale"/> class at the default scale (1.0).</summary>
    public ZoomScale() { }

    /// <summary>Occurs when the zoom scale factor changes.</summary>
    public event Action? Changed;

    /// <summary>Gets or sets the zoom scale factor, clamped between 1.0 and 3.0.</summary>
    public double Value
    {
        get => this.value;
        set
        {
            double clamped = Math.Clamp(value, 1.0, 3.0);
            if (this.value == clamped)
            {
                return;
            }

            this.value = clamped;
            this.Changed?.Invoke();
        }
    }
}
