using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// A layout transformation container that scales its content according to a shared <see cref="ZoomScale"/>
/// and adjusts the scale in response to tunneling Ctrl+wheel gestures.
/// </summary>
public sealed class ZoomHost : LayoutTransformControl
{
    private readonly ZoomScale scale;

    /// <summary>Initializes a new instance of the <see cref="ZoomHost"/> class bound to <paramref name="scale"/>.</summary>
    /// <param name="scale">The shared zoom scale to display and adjust.</param>
    public ZoomHost(ZoomScale scale)
    {
        this.scale = scale;
        this.ApplyScale();
        this.AddHandler(PointerWheelChangedEvent, this.OnPointerWheelTunnel, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.scale.Changed += this.OnScaleChanged;
        this.ApplyScale();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        this.scale.Changed -= this.OnScaleChanged;
    }

    private void OnPointerWheelTunnel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Delta.Y == 0)
        {
            return;
        }

        this.scale.Value = Math.Round(this.scale.Value + e.Delta.Y * 0.1, 1);
        e.Handled = true;
    }

    private void OnScaleChanged() => this.ApplyScale();

    private void ApplyScale() => this.LayoutTransform = new ScaleTransform(this.scale.Value, this.scale.Value);
}
