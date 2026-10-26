using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Whether a piece of text joins a scope's selection.</summary>
public enum TextSelectionMode
{
    /// <summary>Text joins unless it sits inside a control: a button, menu item, combo box, tab, list or tree item, text
    /// box or tooltip.</summary>
    Auto,

    /// <summary>The element and everything inside it stay out of the selection; a press on it starts none.</summary>
    Skip,

    /// <summary>The text joins even inside a control. A press on it still goes to the control.</summary>
    Include,
}

/// <summary>Attached properties that place text in a surface's selection.</summary>
public static class TextSelection
{
    /// <summary>Whether the element's text joins. The nearest element with a value other than Auto decides.</summary>
    public static readonly AttachedProperty<TextSelectionMode> ModeProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, TextSelectionMode>("Mode", typeof(TextSelection));

    /// <summary>True on a container whose text is one block: copied text puts a blank line between it and the text
    /// around it, such as a transcript message or a tool block.</summary>
    public static readonly AttachedProperty<bool> IsBlockProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>("IsBlock", typeof(TextSelection));

    // The scope that holds each window's selection. Weak at both ends, so neither a closed window nor a dropped scope is
    // kept alive by it.
    private static readonly ConditionalWeakTable<TopLevel, WeakReference<TextSelectionScope>> Current = [];

    /// <summary>Whether <paramref name="element"/>'s text joins.</summary>
    public static TextSelectionMode GetMode(AvaloniaObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ModeProperty);
    }

    /// <summary>Sets whether <paramref name="element"/>'s text joins.</summary>
    public static void SetMode(AvaloniaObject element, TextSelectionMode value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ModeProperty, value);
    }

    /// <summary>True when <paramref name="element"/> is one block of copied text.</summary>
    public static bool GetIsBlock(AvaloniaObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsBlockProperty);
    }

    /// <summary>Marks <paramref name="element"/> as one block of copied text.</summary>
    public static void SetIsBlock(AvaloniaObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsBlockProperty, value);
    }

    /// <summary>The scope that holds <paramref name="window"/>'s selection, or null when nothing is selected.</summary>
    public static TextSelectionScope? CurrentScope(TopLevel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Current.TryGetValue(window, out WeakReference<TextSelectionScope>? held) && held.TryGetTarget(out TextSelectionScope? scope)
            && scope.CanCopy ? scope : null;
    }

    // A scope that now holds a selection takes the window's selection; the scope that held it before clears.
    internal static void Take(TopLevel window, TextSelectionScope scope)
    {
        if (Current.TryGetValue(window, out WeakReference<TextSelectionScope>? held) && held.TryGetTarget(out TextSelectionScope? before))
        {
            if (ReferenceEquals(before, scope)) return;
            Current.AddOrUpdate(window, new WeakReference<TextSelectionScope>(scope));
            before.ClearSelection();
            return;
        }
        Current.AddOrUpdate(window, new WeakReference<TextSelectionScope>(scope));
    }

    // A scope whose selection cleared, or that left the window, gives up the window's selection.
    internal static void Release(TopLevel window, TextSelectionScope scope)
    {
        if (Current.TryGetValue(window, out WeakReference<TextSelectionScope>? held) && held.TryGetTarget(out TextSelectionScope? now)
            && ReferenceEquals(now, scope))
            Current.Remove(window);
    }

    // Clears the selection of every scope in the window, before a press starts a new one.
    internal static void ClearWindow(TopLevel window)
    {
        if (Current.TryGetValue(window, out WeakReference<TextSelectionScope>? held) && held.TryGetTarget(out TextSelectionScope? scope))
        {
            Current.Remove(window);
            scope.ClearSelection();
        }
    }
}
