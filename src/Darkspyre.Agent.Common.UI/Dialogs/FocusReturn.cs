using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// Keyboard focus a surface hands back when it goes away. <see cref="Capture"/> records the element the window has focused
/// before a dialog takes focus; <see cref="Track"/> records it each time focus moves into a surface from outside, as for a
/// Prompts panel card. <see cref="Restore"/> returns focus to the recorded element, or to the window's fallback
/// (<see cref="SetFallback"/>) when that element can no longer take focus. The caller restores only when focus was its own
/// to hand back, read with <see cref="IsWithin"/> before it removes the surface, so focus the person moved elsewhere stays
/// where they put it.
/// </summary>
/// <remarks>
/// Focus moves with <see cref="NavigationMethod.Unspecified"/>, so a surface container that holds focus against moves the
/// person did not ask for still refuses it; the fallback then takes it instead.
/// </remarks>
public sealed class FocusReturn
{
    // Each window's fallback, set by the conversation surface that window shows. Keyed by window, so a surface in one window
    // never receives focus handed back in another, and a window going away takes its entry with it.
    private static readonly ConditionalWeakTable<TopLevel, Func<IInputElement?>> Fallbacks = new();
    private WeakReference<IInputElement>? previous;

    private FocusReturn(IInputElement? previous) => this.Record(previous);

    /// <summary>
    /// Sets <paramref name="window"/>'s fallback: the element that takes focus in that window when the recorded one cannot,
    /// such as the selected session's composer. A function that returns null leaves focus as it is.
    /// </summary>
    public static void SetFallback(TopLevel window, Func<IInputElement?> fallback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(fallback);
        Fallbacks.AddOrUpdate(window, fallback);
    }

    /// <summary>
    /// Clears <paramref name="window"/>'s fallback when it is still <paramref name="fallback"/>; a fallback another surface
    /// set since stays.
    /// </summary>
    public static void ClearFallback(TopLevel window, Func<IInputElement?> fallback)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Fallbacks.TryGetValue(window, out Func<IInputElement?>? current) && current == fallback) Fallbacks.Remove(window);
    }

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

    /// <summary>
    /// Returns focus to the recorded element when it can still take focus, otherwise to the fallback of the window that
    /// <paramref name="within"/>, still in the window, belongs to.
    /// </summary>
    public void Restore(Visual within)
    {
        ArgumentNullException.ThrowIfNull(within);
        if (this.RestoreInside(null)) return;
        if (TopLevel.GetTopLevel(within) is { } window && Fallbacks.TryGetValue(window, out Func<IInputElement?>? fallback)
            && Takes(fallback()) is { } target) target.Focus(NavigationMethod.Unspecified);
    }

    /// <summary>
    /// Returns focus to the recorded element when it can still take focus and sits on or inside <paramref name="surface"/>;
    /// any recorded element when <paramref name="surface"/> is null. False when focus did not move there.
    /// </summary>
    public bool RestoreInside(Visual? surface) =>
        this.previous?.TryGetTarget(out IInputElement? recorded) == true && Takes(recorded) is { } target
        && (surface is null || ReferenceEquals(target, surface) || surface.IsVisualAncestorOf(target))
        && target.Focus(NavigationMethod.Unspecified);

    private void Record(IInputElement? element) =>
        this.previous = element is null ? null : new WeakReference<IInputElement>(element);

    private static IInputElement? Focused(Visual anywhere) => TopLevel.GetTopLevel(anywhere)?.FocusManager?.GetFocusedElement();

    // The element when focus can move to it: attached, shown, enabled and focusable.
    private static InputElement? Takes(IInputElement? element) => element is InputElement input && input.IsAttachedToVisualTree()
        && input.IsEffectivelyVisible && input.IsEffectivelyEnabled && input.Focusable ? input : null;
}
