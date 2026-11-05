using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LiveMarkdown.Avalonia;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Darkspyre.Agent.Common.UI.Markdown;

/// <summary>Fits a Markdown table's wrapped cells into weighted shares of the viewer's width.</summary>
internal sealed class FittedTableNode : BlockNode<Table>
{
    private readonly Grid grid = new();

    /// <summary>Creates the fitted table grid.</summary>
    public FittedTableNode() => this.grid.Classes.Add("fitted-table");

    /// <inheritdoc />
    public override Control Control => this.grid;

    /// <inheritdoc />
    protected override bool UpdateCore(DocumentNode documentNode, Table table, in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        this.grid.Children.Clear();
        this.grid.ColumnDefinitions.Clear();
        this.grid.RowDefinitions.Clear();
        TableRow[] rows = [.. table.OfType<TableRow>()];
        int columns = 0;
        foreach (TableRow row in rows)
            foreach ((TableCell cell, int column) in Cells(row))
                columns = Math.Max(columns, column + cell.ColumnSpan);
        int[] weights = [.. Enumerable.Repeat(4, columns)];
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            TableRow row = rows[rowIndex];
            this.grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach ((TableCell cell, int column) in Cells(row))
            {
                MarkdownTextBlock text = new() { TextWrapping = TextWrapping.Wrap, Inlines = [] };
                foreach (LeafBlock leaf in cell.Descendants<LeafBlock>())
                {
                    if (leaf.Inline is not { } inlines) continue;
                    if (text.Inlines.Count > 0) text.Inlines.Add(new LineBreak());
                    text.Inlines.Add(new CellInlines().Render(documentNode, inlines, in change, cancellationToken));
                }
                if (row.IsHeader) text.Classes.Add("header");
                if (table.ColumnDefinitions is { } definitions && column < definitions.Count)
                    text.TextAlignment = definitions[column].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    };
                weights[column] = Math.Max(weights[column], Weight(text.ActualText));
                Border border = new() { Child = text };
                border.Classes.Add("fitted-table-cell");
                Grid.SetColumn(border, column);
                Grid.SetColumnSpan(border, cell.ColumnSpan);
                Grid.SetRow(border, rowIndex);
                Grid.SetRowSpan(border, cell.RowSpan);
                this.grid.Children.Add(border);
            }
        }
        foreach (int weight in weights)
            this.grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)));
        return true;
    }

    // Parsed pipe tables leave ColumnIndex unassigned; their cells follow column-span order.
    private static IEnumerable<(TableCell Cell, int Column)> Cells(TableRow row)
    {
        int next = 0;
        foreach (TableCell cell in row.OfType<TableCell>())
        {
            int column = cell.ColumnIndex >= 0 ? cell.ColumnIndex : next;
            yield return (cell, column);
            next = column + cell.ColumnSpan;
        }
    }

    // The container extension keeps the library's registered child inline renderers in each cell.
    private sealed class CellInlines() : InlinesNode<Markdig.Syntax.Inlines.ContainerInline>(new Span())
    {
        internal Avalonia.Controls.Documents.Inline Render(DocumentNode documentNode, Markdig.Syntax.Inlines.ContainerInline inlines,
            in ObservableStringBuilderChangedEventArgs change, CancellationToken cancellationToken)
        {
            base.UpdateCore(documentNode, inlines, in change, cancellationToken);
            return this.Inline;
        }
    }

    private static int Weight(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char character in text)
        {
            run = char.IsWhiteSpace(character) ? 0 : run + 1;
            longest = Math.Max(longest, run);
        }
        return Math.Clamp(longest, 4, 40);
    }
}
