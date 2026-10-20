using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// A round status light. Each light pulses at its own pace and offset, derived from a stable hash of its key, so the
/// lights side by side are out of step and keep their rhythm across redraws.
/// </summary>
public sealed class StatusLight : Decorator
{
    private readonly Ellipse dot = new() { Width = 10, Height = 10 };
    private readonly Border glow = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5) };
    private static readonly TimeSpan FallbackBlink = TimeSpan.FromSeconds(1.1);

    /// <summary>Creates the light for <paramref name="light"/>, named <paramref name="label"/>, pulsing by <paramref name="pulseKey"/>.</summary>
    public StatusLight(StatusLightState light, string label, string pulseKey)
    {
        this.Light = light;
        this.PulseKey = pulseKey;
        this.Width = 24;
        this.Height = 24;
        ToolTip.SetTip(this, label);
        Avalonia.Automation.AutomationProperties.SetName(this, label);
        Grid layers = new();
        layers.Children.Add(this.glow);
        layers.Children.Add(this.dot);
        this.Child = layers;
        this.Classes.Add("status-light");
        this.Classes.Add(ClassOf(light));
        this.dot.Classes.Add("status-dot");
        this.glow.Classes.Add("status-glow");
        // An animation cannot start partway into its cycle, so the light waits for the rest of the cycle instead; after
        // that wait it pulses in the same step as one that started at the offset.
        (TimeSpan duration, TimeSpan into) = PulseOf(pulseKey);
        Animation? animation = light switch
        {
            StatusLightState.Active or StatusLightState.Failed => Glow(duration, Wait(duration, into)),
            StatusLightState.Fallback => Blink(FallbackBlink, Wait(FallbackBlink, into)),
            StatusLightState.Off => null,
            _ => Blink(TimeSpan.FromSeconds(0.6), TimeSpan.Zero),
        };
        if (animation is not null) LoopingAnimation.Play(this, animation);
    }

    /// <summary>The light shown.</summary>
    public StatusLightState Light { get; }

    /// <summary>The stable key the pulse pace and offset derive from.</summary>
    public string PulseKey { get; }

    /// <summary>The style class of a light.</summary>
    public static string ClassOf(StatusLightState light) => light switch
    {
        StatusLightState.Active => "light-active",
        StatusLightState.Off => "light-off",
        StatusLightState.Failed => "light-failed",
        StatusLightState.Fallback => "light-fallback",
        _ => "light-busy",
    };

    /// <summary>
    /// The pulse of <paramref name="key"/>: FNV-1a over the key gives a period from 2.6 s to 4.2 s and a point from 0 s
    /// to 4 s into the cycle where the light starts.
    /// </summary>
    public static (TimeSpan Duration, TimeSpan Offset) PulseOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        uint hash = 2166136261;
        foreach (char item in key) hash = (hash ^ item) * 16777619;
        return (TimeSpan.FromSeconds(2.6 + (hash % 1601 / 1000.0)), TimeSpan.FromSeconds(hash / 1601 % 4001 / 1000.0));
    }

    private static TimeSpan Wait(TimeSpan period, TimeSpan into) => TimeSpan.FromTicks((period.Ticks - into.Ticks % period.Ticks) % period.Ticks);

    // The glow grows and the light brightens at mid-cycle.
    private static Animation Glow(TimeSpan duration, TimeSpan offset) => new()
    {
        Duration = duration,
        Delay = offset,
        IterationCount = IterationCount.Infinite,
        Easing = new SineEaseInOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.82) } },
            new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.82) } },
        },
    };

    // A step blink: full for half the period, a quarter for the rest.
    private static Animation Blink(TimeSpan duration, TimeSpan offset) => new()
    {
        Duration = duration,
        Delay = offset,
        IterationCount = IterationCount.Infinite,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(0.4999), Setters = { new Setter(OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(OpacityProperty, 0.25) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.25) } },
        },
    };
}
