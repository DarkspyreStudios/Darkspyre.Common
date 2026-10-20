using Avalonia;
using Avalonia.Animation;
using Avalonia.Styling;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Plays an animation that repeats forever on a control.</summary>
public static class LoopingAnimation
{
    /// <summary>
    /// Plays <paramref name="animation"/> on <paramref name="target"/> while the target is in the visual tree. Avalonia
    /// refuses <see cref="Animation.RunAsync(Animatable, CancellationToken)"/> for an animation with an infinite
    /// iteration count, so the animation runs from a style in the target's own styles.
    /// </summary>
    /// <gotcha>The style matches by type, so a descendant of the same type as <typeparamref name="T"/> plays it too.</gotcha>
    public static void Play<T>(T target, Animation animation) where T : StyledElement
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(animation);
        target.Styles.Add(new Style(selector => selector.OfType<T>()) { Animations = { animation } });
    }
}
