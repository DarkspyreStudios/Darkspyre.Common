using Avalonia.Controls;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Errors;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The Copy items at the top of a row's right-click menu.</summary>
public static class RowCopy
{
    /// <summary>Inserts, at the top of <paramref name="menu"/>, "Copy" (the row's text, <see cref="TextOf"/>), then each of
    /// <paramref name="items"/>, then a separator when the menu already had items.</summary>
    /// <param name="menu">The row's menu.</param>
    /// <param name="row">The row; copies are requested from it.</param>
    /// <param name="items">Further items in order, such as ("Copy path", …); a value that completes null or empty copies
    /// nothing.</param>
    /// <gotcha>A value that throws copies nothing and shows its failure in the row's nearest host region.</gotcha>
    public static void Add(ContextMenu menu, Control row, params (string Header, Func<Task<string?>> Value)[] items)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(items);
        bool hadItems = menu.Items.Count > 0;
        int index = 0;
        menu.Items.Insert(index++, Item(row, "Copy", () => Task.FromResult<string?>(TextOf(row))));
        foreach ((string header, Func<Task<string?>> value) in items) menu.Items.Insert(index++, Item(row, header, value));
        if (hadItems) menu.Items.Insert(index, new Separator());
    }

    /// <summary>A menu holding only the Copy items, for a row with no actions of its own.</summary>
    /// <param name="row">The row; copies are requested from it.</param>
    /// <param name="items">Further items in order after "Copy".</param>
    public static ContextMenu Menu(Control row, params (string Header, Func<Task<string?>> Value)[] items)
    {
        ContextMenu menu = new();
        Add(menu, row, items);
        return menu;
    }

    /// <summary>The row's text as shown, on one line: its visible <c>TextBlock</c>s in reading order, joined by one
    /// space.</summary>
    /// <param name="row">The row.</param>
    public static string TextOf(Control row)
    {
        ArgumentNullException.ThrowIfNull(row);
        IEnumerable<string> texts = row.GetSelfAndVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => TextSelectionScope.TextOf(block).ReplaceLineEndings(" ").Trim())
            .Where(text => text.Length > 0);
        return string.Join(' ', texts);
    }

    private static MenuItem Item(Control row, string header, Func<Task<string?>> value)
    {
        MenuItem item = new() { Header = header };
        item.Click += async (_, _) =>
        {
            try
            {
                if (await value() is { Length: > 0 } text) await TextCopy.RequestAsync(row, text);
            }
            catch (Exception error) when (error is not OperationCanceledException) { UiRegion.Report(row, error); }
        };
        return item;
    }
}
