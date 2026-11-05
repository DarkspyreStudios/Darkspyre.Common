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
    private bool isSubscribed;

    /// <summary>Initializes a new instance of the <see cref="ZoomHost"/> class bound to <paramref name="scale"/>.</summary>
    /// <param name="scale">The shared zoom scale to display and adjust.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    public ZoomHost(ZoomScale scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        this.scale = scale;
        this.ApplyScale();
        this.AddHandler(PointerWheelChangedEvent, this.OnPointerWheelTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Gets the shared zoom scale governing this host.</summary>
    public ZoomScale Scale => this.scale;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!this.isSubscribed)
        {
            this.scale.Changed += this.OnScaleChanged;
            this.isSubscribed = true;
        }

        this.ApplyScale();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (this.isSubscribed)
        {
            this.scale.Changed -= this.OnScaleChanged;
            this.isSubscribed = false;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.Handled)
        {
            this.HandleWheel(e);
        }
    }

    private void OnPointerWheelTunnel(object? sender, PointerWheelEventArgs e) => this.HandleWheel(e);

    private void HandleWheel(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Delta.Y == 0)
        {
            return;
        }

        this.scale.Value += e.Delta.Y * ZoomScale.Step;
        this.ApplyScale();
        e.Handled = true;
    }

    private void OnScaleChanged() => this.ApplyScale();

    private void ApplyScale()
    {
        this.LayoutTransform = new ScaleTransform(this.scale.Value, this.scale.Value);
    }
}
