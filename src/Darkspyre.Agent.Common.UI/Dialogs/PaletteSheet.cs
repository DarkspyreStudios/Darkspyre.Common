using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>Everything reachable by name: commands, sessions and projects, filtered by what the person types.</summary>
public static class PaletteSheet
{
    /// <summary>One thing the palette can act on.</summary>
    /// <param name="Section">The heading it appears under.</param>
    /// <param name="Label">What it is called.</param>
    /// <param name="Detail">Where it is, or what it does.</param>
    /// <param name="Act">What choosing it does.</param>
    public sealed record PaletteItem(string Section, string Label, string Detail, Func<Task> Act);

    /// <summary>
    /// Opens the palette over <paramref name="items"/>. Choosing an item closes the palette, then acts; closing it any
    /// other way runs <paramref name="closed"/>.
    /// </summary>
    public static Sheet Open(ModalLayer layer, IReadOnlyList<PaletteItem> items, Func<Task> closed)
    {
        TextBox search = new() { PlaceholderText = "Search commands, sessions and projects…" };
        search.Classes.Add("palette-input");
        // A row recycled when the filter replaces the items is built again with no row; it shows nothing.
        ListBox list = new() { ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? null : RowView(row)) };
        list.Classes.Add("palette-list");
        TextBlock empty = new() { Text = "Nothing matches." };
        empty.Classes.Add("palette-empty");
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(search);
        body.Children.Add(empty);
        body.Children.Add(list);
        Sheet sheet = null!;
        sheet = new Sheet(layer, "Go to", body, kind: "palette", targetName: "command-palette") { Dismissed = closed };

        void Filter()
        {
            string query = search.Text ?? string.Empty;
            List<Row> rows = [];
            foreach (IGrouping<string, PaletteItem> section in items.GroupBy(item => item.Section, StringComparer.Ordinal))
            {
                // A session is found by its project as well as its name, so both are what the search reads.
                PaletteItem[] matching = [.. section.Where(item => Searchable(item).Contains(query, StringComparison.OrdinalIgnoreCase))];
                if (matching.Length == 0) continue;
                rows.Add(new(section.Key, null));
                rows.AddRange(matching.Select(item => new Row(section.Key, item)));
            }
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(row => row.Item is not null);
            empty.IsVisible = rows.Count == 0;
            list.IsVisible = rows.Count > 0;
        }

        async Task ChooseAsync()
        {
            if (list.SelectedItem is not Row { Item: { } chosen }) return;
            sheet.Answered();
            sheet.Close();
            await closed();
            await chosen.Act();
        }

        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Down or Key.Up)
            {
                Move(list, args.Key == Key.Down ? 1 : -1);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter)
            {
                _ = ChooseAsync();
                args.Handled = true;
            }
        };
        list.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            _ = ChooseAsync();
            args.Handled = true;
        };
        list.SelectionChanged += (_, _) =>
        {
            // A heading is never chosen; moving onto one moves past it.
            if (list.SelectedItem is Row { Item: null }) Move(list, 1);
        };
        list.Tapped += (_, _) => _ = ChooseAsync();
        Filter();
        sheet.Show();
        search.AttachedToVisualTree += (_, _) => search.Focus();
        return sheet;
    }

    private static string Searchable(PaletteItem item) => item.Detail.Length == 0 ? item.Label : $"{item.Label} {item.Detail}";

    private static void Move(ListBox list, int step)
    {
        if (list.ItemsSource is not IList<Row> rows || rows.Count == 0) return;
        int index = list.SelectedIndex;
        for (int i = 0; i < rows.Count; i++)
        {
            index = (index + step + rows.Count) % rows.Count;
            if (rows[index].Item is not null)
            {
                list.SelectedIndex = index;
                list.ScrollIntoView(index);
                return;
            }
        }
    }

    private static Control RowView(Row row)
    {
        if (row.Item is not { } item)
        {
            TextBlock heading = new() { Text = row.Section };
            heading.Classes.Add("palette-heading");
            return heading;
        }
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        TextBlock label = new() { Text = item.Label };
        label.Classes.Add("palette-label");
        line.Children.Add(label);
        if (item.Detail.Length > 0)
        {
            TextBlock detail = new() { Text = item.Detail };
            detail.Classes.Add("palette-detail");
            line.Children.Add(detail);
        }
        return line;
    }

    private sealed record Row(string Section, PaletteItem? Item);
}
