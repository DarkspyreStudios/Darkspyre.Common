using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Errors;

/// <summary>
/// The card a failed area shows in its own place: an optional title, a sentence, the caller's actions with Show
/// details, an optional status line, and the hidden details: a label and value table and the stack trace.
/// </summary>
public sealed class FailureView : UserControl
{
    /// <summary>Creates the card.</summary>
    /// <param name="title">The bold title, such as the failed plugin's name; empty for none.</param>
    /// <param name="message">The sentence that says what stopped working.</param>
    /// <param name="details">The rows of the details table, in order.</param>
    /// <param name="stackTrace">The stack trace shown under the table, or null.</param>
    /// <param name="actions">The buttons before Show details, in order.</param>
    /// <param name="detailsAction">A control shown at the end of the details, such as a Copy button, or null.</param>
    /// <param name="subtitle">Muted text beside the title, such as a version, or null.</param>
    /// <param name="status">A line under the actions that the caller shows and hides, such as an action's error, or null.</param>
    /// <param name="kind">The style class of the card's frame.</param>
    public FailureView(string title, string message, IReadOnlyList<(string Label, string Value)> details,
        string? stackTrace, IReadOnlyList<Control> actions, Control? detailsAction = null, string? subtitle = null,
        Control? status = null, string kind = "surface-failure-card")
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(actions);
        StackPanel card = new() { Spacing = 8 };
        if (title.Length > 0)
        {
            StackPanel heading = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            heading.Children.Add(Forms.Text(title, "surface-failure-title"));
            if (subtitle is not null) heading.Children.Add(Forms.Text(subtitle, "surface-failure-version"));
            card.Children.Add(heading);
        }
        card.Children.Add(Forms.Text(message, "surface-failure-sentence"));
        StackPanel table = Details(details, stackTrace);
        table.IsVisible = false;
        if (detailsAction is not null) table.Children.Add(detailsAction);
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Classes.Add("surface-failure-actions");
        foreach (Control action in actions) row.Children.Add(action);
        Button toggle = new() { Content = "Show details" };
        toggle.Classes.Add("ghost");
        toggle.Click += (_, _) =>
        {
            table.IsVisible = !table.IsVisible;
            toggle.Content = table.IsVisible ? "Hide details" : "Show details";
        };
        row.Children.Add(toggle);
        card.Children.Add(row);
        if (status is not null) card.Children.Add(status);
        card.Children.Add(table);
        Border frame = new() { Child = card };
        frame.Classes.Add(kind);
        this.Content = frame;
    }

    private static StackPanel Details(IReadOnlyList<(string Label, string Value)> rows, string? stack)
    {
        StackPanel details = new() { Spacing = 4 };
        details.Classes.Add("surface-failure-details");
        Grid table = new() { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        for (int row = 0; row < rows.Count; row++)
        {
            table.RowDefinitions.Add(new(GridLength.Auto));
            TextBlock name = Forms.Text(rows[row].Label, "surface-failure-label");
            Grid.SetRow(name, row);
            TextBlock content = new() { Text = rows[row].Value, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            table.Children.Add(name);
            table.Children.Add(content);
        }
        details.Children.Add(table);
        if (stack is { Length: > 0 })
        {
            TextBlock trace = new() { Text = stack, TextWrapping = TextWrapping.Wrap };
            trace.Classes.Add("surface-failure-stack");
            details.Children.Add(trace);
        }
        return details;
    }
}
