using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Terminal;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The application's right-click menus for text.</summary>
public static class SelectionMenu
{
    private static MenuFlyout? text;
    private static MenuFlyout? textBox;
    private static MenuFlyout? terminal;

    /// <summary>The one selection menu: Copy and Select All, each showing the platform's key. Copy is enabled while the
    /// window has a selection and copies <see cref="TextSelection.CurrentScope"/>; Select All selects all text of the
    /// nearest <see cref="TextSelectionScope"/> of the control it opened on (<c>FlyoutBase.Target</c>), so it works with
    /// nothing selected. When it opened on a view that copies its own text, both act on that view's
    /// <see cref="IEditCommandTarget"/>.</summary>
    /// <gotcha>One instance serves every text block: a <c>SelectableTextBlock</c> keeps its selection on focus loss only
    /// while its own <c>ContextFlyout</c> is open, and with one shared instance that holds for every block.</gotcha>
    public static MenuFlyout Text => text ??= CreateText();

    /// <summary>The one text-box menu: Cut, Copy, Paste and Select All, each showing the platform's key, enabled from the
    /// box's <c>CanCut</c>, <c>CanCopy</c> and <c>CanPaste</c> as it opens. In a secret box (<c>PasswordChar</c> set, revealed
    /// or not) Cut and Copy are always disabled: Avalonia's own <c>CanCopy</c> turns true once a box is revealed.</summary>
    /// <gotcha>Shown at an <see cref="ITextEditTarget"/>, such as a web page's text field, it acts on that target.</gotcha>
    public static MenuFlyout TextBox => textBox ??= CreateTextBox();

    /// <summary>The terminal's menu: Copy, Paste and Select All, each showing the terminal's own key.</summary>
    public static MenuFlyout Terminal => terminal ??= CreateTerminal();

    private static MenuFlyout CreateText()
    {
        MenuItem copy = new() { Header = "Copy" }, selectAll = new() { Header = "Select All" };
        MenuFlyout menu = new() { Items = { copy, selectAll } };
        IEditCommandTarget? target = null;
        TopLevel? window = null;
        TextSelectionScope? nearest = null;
        menu.Opening += (_, _) =>
        {
            Control? on = menu.Target;
            target = on is null ? null : EditRouting.TargetOf(on);
            window = on is null ? null : TopLevel.GetTopLevel(on);
            nearest = on?.GetSelfAndVisualAncestors().OfType<TextSelectionScope>().FirstOrDefault();
            PlatformHotkeyConfiguration? keys = EditRouting.Keys;
            copy.InputGesture = keys?.Copy.FirstOrDefault();
            selectAll.InputGesture = keys?.SelectAll.FirstOrDefault();
            copy.IsEnabled = target?.CanCopy ?? (window is not null && TextSelection.CurrentScope(window) is not null);
            selectAll.IsEnabled = target is not null || nearest is not null;
        };
        copy.Click += (_, _) =>
        {
            if (target is not null) target.Copy();
            else if (window is not null) TextSelection.CurrentScope(window)?.Copy();
        };
        selectAll.Click += (_, _) =>
        {
            if (target is not null) target.SelectAll();
            else nearest?.SelectAll();
        };
        return menu;
    }

    private static MenuFlyout CreateTextBox()
    {
        MenuItem cut = new() { Header = "Cut" }, copy = new() { Header = "Copy" }, paste = new() { Header = "Paste" }, selectAll = new() { Header = "Select All" };
        MenuFlyout menu = new() { Items = { cut, copy, paste, selectAll } };
        TextBox? box = null;
        ITextEditTarget? editable = null;
        menu.Opening += (_, _) =>
        {
            Control? on = menu.Target;
            box = on?.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault();
            editable = box is null ? on as ITextEditTarget : null;
            PlatformHotkeyConfiguration? keys = EditRouting.Keys;
            cut.InputGesture = keys?.Cut.FirstOrDefault();
            copy.InputGesture = keys?.Copy.FirstOrDefault();
            paste.InputGesture = keys?.Paste.FirstOrDefault();
            selectAll.InputGesture = keys?.SelectAll.FirstOrDefault();
            bool secret = box is not null && box.PasswordChar != default;
            cut.IsEnabled = !secret && (box?.CanCut ?? editable?.CanCut ?? false);
            copy.IsEnabled = !secret && (box?.CanCopy ?? editable?.CanCopy ?? false);
            paste.IsEnabled = box?.CanPaste ?? editable?.CanPaste ?? false;
            selectAll.IsEnabled = box is not null || editable is not null;
        };
        cut.Click += (_, _) =>
        {
            if (box is not null) box.Cut();
            else editable?.Cut();
        };
        copy.Click += (_, _) =>
        {
            if (box is not null) box.Copy();
            else editable?.Copy();
        };
        paste.Click += (_, _) =>
        {
            if (box is not null) box.Paste();
            else editable?.Paste();
        };
        selectAll.Click += (_, _) =>
        {
            if (box is not null) box.SelectAll();
            else editable?.SelectAll();
        };
        return menu;
    }

    private static MenuFlyout CreateTerminal()
    {
        MenuItem copy = new() { Header = "Copy" }, paste = new() { Header = "Paste" }, selectAll = new() { Header = "Select All" };
        MenuFlyout menu = new() { Items = { copy, paste, selectAll } };
        TerminalView? view = null;
        menu.Opening += (_, _) =>
        {
            view = menu.Target?.GetSelfAndVisualAncestors().OfType<TerminalView>().FirstOrDefault();
            TerminalKeys? keys = view?.Keys;
            copy.InputGesture = keys?.Copy;
            paste.InputGesture = keys?.Paste;
            selectAll.InputGesture = keys?.SelectAll;
            copy.IsEnabled = view?.CanCopy ?? false;
            paste.IsEnabled = selectAll.IsEnabled = view is not null;
        };
        copy.Click += (_, _) => view?.Copy();
        paste.Click += (_, _) => view?.Paste();
        selectAll.Click += (_, _) => view?.SelectAll();
        return menu;
    }
}
