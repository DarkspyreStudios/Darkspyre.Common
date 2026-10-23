using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The context menu of selectable text: Copy and Select All.</summary>
public static class SelectionMenu
{
    /// <summary>A menu of Copy and Select All. Copy is enabled only while <paramref name="canCopy"/> is true as the menu opens.</summary>
    /// <param name="canCopy">True while there is a selection to copy.</param>
    /// <param name="copy">Writes the selection to the clipboard.</param>
    /// <param name="selectAll">Selects the whole text.</param>
    /// <gotcha>Set the menu as the text control's <c>ContextFlyout</c>. It opens when a right-click raises <c>ContextRequested</c>
    /// on that control or a control inside it; a control that ends its pointer handling before Avalonia raises that request
    /// opens nothing until its owner raises the request itself, as <c>MarkdownViewer</c> does.</gotcha>
    public static MenuFlyout Create(Func<bool> canCopy, Action copy, Action selectAll)
    {
        ArgumentNullException.ThrowIfNull(canCopy);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(selectAll);
        MenuItem copyItem = new() { Header = "Copy" };
        copyItem.Click += (_, _) => copy();
        MenuItem selectAllItem = new() { Header = "Select All" };
        selectAllItem.Click += (_, _) => selectAll();
        MenuFlyout menu = new() { Items = { copyItem, selectAllItem } };
        menu.Opening += (_, _) => copyItem.IsEnabled = canCopy();
        return menu;
    }
}
