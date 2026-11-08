using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// Keyboard focus a surface hands back when it goes away. <see cref="Capture"/> records the element the window has focused
/// before a dialog takes focus; <see cref="Track"/> records it each time focus moves into a surface from outside, as for a
/// Prompts panel card. <see cref="Restore"/> returns focus to the recorded element, or to <see cref="Fallback"/> when that
/// element can no longer take focus. The caller restores only when focus was its own to hand back, read with
/// <see cref="IsWithin"/> before it removes the surface, so focus the person moved elsewhere stays where they put it.
/// </summary>
/// <remarks>
/// Focus moves with <see cref="NavigationMethod.Unspecified"/>, so a surface container that holds focus against moves the
/// person did not ask for still refuses it; the fallback then takes it instead.
/// </remarks>
public sealed class FocusReturn
{
    private WeakReference<IInputElement>? previous;

    private FocusReturn(IInputElement? previous) => this.Record(previous);

    /// <summary>
    /// The element that takes focus when the recorded one cannot, such as the selected session's composer. The shell sets
    /// it once. Null, or a function that returns null, leaves focus as it is.
    /// </summary>
    public static Func<IInputElement?>? Fallback { get; set; }

    /// <summary>Records the element <paramref name="anywhere"/>'s window has focused now; none when it is not in a window.</summary>
    public static FocusReturn Capture(Visual anywhere)
    {
        ArgumentNullException.ThrowIfNull(anywhere);
        return new(Focused(anywhere));
    }

    /// <summary>
    /// Records, each time focus is about to move into <paramref name="surface"/> from outside it, the element focused
    /// before. Moves within the surface keep the record.
    /// </summary>
    public static FocusReturn Track(InputElement surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        FocusReturn returning = new(null);
        // GettingFocus is raised before focus moves and bubbles from the element that takes it, so the window still has the
        // element focus is leaving.
        surface.AddHandler(InputElement.GettingFocusEvent, (_, _) =>
        {
            if (!IsWithin(surface)) returning.Record(Focused(surface));
        }, RoutingStrategies.Bubble);
        return returning;
    }

    /// <summary>True when <paramref name="surface"/>'s window has focus on <paramref name="surface"/> or inside it.</summary>
    public static bool IsWithin(Visual surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return Focused(surface) is Visual visual && (ReferenceEquals(visual, surface) || surface.IsVisualAncestorOf(visual));
    }

    /// <summary>True when <paramref name="anywhere"/>'s window has no element focused.</summary>
    public static bool IsNowhere(Visual anywhere)
    {
        ArgumentNullException.ThrowIfNull(anywhere);
        return Focused(anywhere) is null;
    }

    /// <summary>Returns focus to the recorded element when it can still take focus, otherwise to <see cref="Fallback"/>.</summary>
    public void Restore()
    {
        if (this.previous?.TryGetTarget(out IInputElement? recorded) == true && Takes(recorded) is { } target
            && target.Focus(NavigationMethod.Unspecified)) return;
        if (Takes(Fallback?.Invoke()) is { } fallback) fallback.Focus(NavigationMethod.Unspecified);
    }

    private void Record(IInputElement? element) =>
        this.previous = element is null ? null : new WeakReference<IInputElement>(element);

    private static IInputElement? Focused(Visual anywhere) => TopLevel.GetTopLevel(anywhere)?.FocusManager?.GetFocusedElement();

    // The element when focus can move to it: attached, shown, enabled and focusable.
    private static InputElement? Takes(IInputElement? element) => element is InputElement input && input.IsAttachedToVisualTree()
        && input.IsEffectivelyVisible && input.IsEffectivelyEnabled && input.Focusable ? input : null;
}
