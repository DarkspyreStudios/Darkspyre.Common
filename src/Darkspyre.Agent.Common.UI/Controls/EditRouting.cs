using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Dialogs;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>An edit command from a key, a context menu or the application's Edit menu.</summary>
public enum EditCommand
{
    /// <summary>Undo the last edit.</summary>
    Undo,

    /// <summary>Redo the last undone edit.</summary>
    Redo,

    /// <summary>Copy the selection and remove it.</summary>
    Cut,

    /// <summary>Copy the selection.</summary>
    Copy,

    /// <summary>Insert the clipboard's text.</summary>
    Paste,

    /// <summary>Remove the selection.</summary>
    Delete,

    /// <summary>Select all text.</summary>
    SelectAll,
}

/// <summary>A view that copies and selects its own text when an edit command reaches it.</summary>
public interface IEditCommandTarget
{
    /// <summary>True while there is a selection to copy.</summary>
    bool CanCopy { get; }

    /// <summary>Copies the selection through <see cref="TextCopy"/>.</summary>
    void Copy();

    /// <summary>Selects all of the view's text.</summary>
    void SelectAll();
}

/// <summary>A view whose text can also be cut and pasted, such as a web page's text field.</summary>
public interface ITextEditTarget : IEditCommandTarget
{
    /// <summary>True while a cut would remove text.</summary>
    bool CanCut { get; }

    /// <summary>True while the view takes pasted text.</summary>
    bool CanPaste { get; }

    /// <summary>Copies the selection through <see cref="TextCopy"/> and removes it once the copy succeeds.</summary>
    void Cut();

    /// <summary>Inserts the clipboard's text.</summary>
    void Paste();
}

/// <summary>Sends an edit command to the one place in a window that should take it.</summary>
public static class EditRouting
{
    /// <summary>Runs <paramref name="command"/> in <paramref name="window"/>. Returns false when nothing took it.</summary>
    /// <param name="window">The window whose focused element decides where the command goes.</param>
    /// <param name="command">The command to run.</param>
    /// <remarks>Order: the focused <c>TextBox</c> takes every command; else the first <see cref="IEditCommandTarget"/> on the
    /// focused element or its visual ancestors that is not a <see cref="TextSelectionScope"/> takes Copy and Select All
    /// (and Cut and Paste when it is an <see cref="ITextEditTarget"/>); else Copy goes to the window's selection
    /// (<see cref="TextSelection.CurrentScope"/>). Select All goes to the nearest <see cref="TextSelectionScope"/> of the
    /// focused element first (the scope a press last focused, or the scope a sheet or dialog's focused control sits in).
    /// Only when the focused element has no scope does it go to the window's selection, and then only when that
    /// selection's scope is in the focused layer: the top open sheet or dialog of the window's modal layer when one is
    /// open, else the window. A sheet or dialog therefore never selects the surface behind it.</remarks>
    public static bool Run(TopLevel window, EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(window);
        IInputElement? focused = window.FocusManager?.GetFocusedElement();
        if (focused is TextBox box)
        {
            RunOn(box, command);
            return true;
        }
        Visual? visual = focused as Visual;
        if (visual is not null && TargetOf(visual) is { } target)
        {
            switch (command)
            {
                case EditCommand.Copy: target.Copy(); return true;
                case EditCommand.SelectAll: target.SelectAll(); return true;
                case EditCommand.Cut when target is ITextEditTarget editable: editable.Cut(); return true;
                case EditCommand.Paste when target is ITextEditTarget editable: editable.Paste(); return true;
                default: return false;
            }
        }
        if (command == EditCommand.Copy)
        {
            if (TextSelection.CurrentScope(window) is not { } selection) return false;
            selection.Copy();
            return true;
        }
        if (command != EditCommand.SelectAll) return false;
        if (visual?.GetSelfAndVisualAncestors().OfType<TextSelectionScope>().FirstOrDefault() is { } nearest)
        {
            nearest.SelectAll();
            return true;
        }
        if (TextSelection.CurrentScope(window) is not { } current || !FocusedLayer(window).IsVisualAncestorOf(current)) return false;
        current.SelectAll();
        return true;
    }

    /// <summary>The first view on <paramref name="visual"/>'s path that copies its own text, scopes left out.</summary>
    internal static IEditCommandTarget? TargetOf(Visual visual) =>
        visual.GetSelfAndVisualAncestors().OfType<IEditCommandTarget>().FirstOrDefault(found => found is not TextSelectionScope);

    /// <summary>The platform's edit keys, from the application's platform settings.</summary>
    internal static PlatformHotkeyConfiguration? Keys => Application.Current?.PlatformSettings?.HotkeyConfiguration;

    /// <summary>Installs the window handlers once: the Copy and Select All keys, and the copies text boxes and selectable
    /// text blocks make themselves.</summary>
    internal static void Install()
    {
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>(KeyDown, RoutingStrategies.Tunnel);
        TextBox.CopyingToClipboardEvent.AddClassHandler<TopLevel>((_, e) => CopyFromBox(e, cut: false));
        TextBox.CuttingToClipboardEvent.AddClassHandler<TopLevel>((_, e) => CopyFromBox(e, cut: true));
        SelectableTextBlock.CopyingToClipboardEvent.AddClassHandler<TopLevel>(CopyFromBlock);
    }

    private static void RunOn(TextBox box, EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo: box.Undo(); break;
            case EditCommand.Redo: box.Redo(); break;
            case EditCommand.Cut: box.Cut(); break;
            case EditCommand.Copy: box.Copy(); break;
            case EditCommand.Paste: box.Paste(); break;
            case EditCommand.Delete: box.SelectedText = string.Empty; break;
            case EditCommand.SelectAll: box.SelectAll(); break;
        }
    }

    // The top open sheet or dialog of the window's modal layer, else the window.
    private static Visual FocusedLayer(TopLevel window) =>
        window.GetVisualDescendants().OfType<ModalLayer>().FirstOrDefault()?.TopDialog ?? window;

    // The window's Copy and Select All keys, unless a text box or a view with its own keys is focused.
    private static void KeyDown(TopLevel window, KeyEventArgs e)
    {
        if (e.Handled || Keys is not { } keys) return;
        EditCommand command;
        if (keys.Copy.Any(gesture => gesture.Matches(e))) command = EditCommand.Copy;
        else if (keys.SelectAll.Any(gesture => gesture.Matches(e))) command = EditCommand.SelectAll;
        else return;
        if (window.FocusManager?.GetFocusedElement() is Visual focused
            && focused.GetSelfAndVisualAncestors().Any(element => element is TextBox || (element is IEditCommandTarget and not TextSelectionScope))) return;
        Run(window, command);
        e.Handled = true;
    }

    // A text box's own copy or cut goes through the one copy request. A secret box copies and cuts nothing, revealed or not.
    private static void CopyFromBox(RoutedEventArgs e, bool cut)
    {
        if (e.Source is not TextBox box) return;
        e.Handled = true;
        if (box.PasswordChar != default) return;
        _ = CopyAsync(box, box.SelectedText, cut);
    }

    private static async Task CopyAsync(TextBox box, string text, bool cut)
    {
        if (await TextCopy.RequestAsync(box, text) && cut) box.SelectedText = string.Empty;
    }

    // A selectable text block's own copy never writes: the window's selection goes through the one copy request, or the
    // block's own selection when no scope holds one.
    private static void CopyFromBlock(TopLevel window, RoutedEventArgs e)
    {
        if (e.Source is not SelectableTextBlock block) return;
        e.Handled = true;
        if (TextSelection.CurrentScope(window) is { } scope) scope.Copy();
        else _ = TextCopy.RequestAsync(block, block.SelectedText);
    }
}
