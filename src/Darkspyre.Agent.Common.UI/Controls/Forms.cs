using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Darkspyre.Agent.Common.UI.Errors;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The size of a library button.</summary>
public enum ButtonSize
{
    /// <summary>32 px high.</summary>
    Default,

    /// <summary>28 px high.</summary>
    Small,

    /// <summary>24 px high.</summary>
    ExtraSmall,

    /// <summary>36 px high.</summary>
    Large,
}

/// <summary>
/// Builds the pieces every form and settings view is made of, with the style classes the control styles give them, so
/// every view draws a heading, a note, a field or a button the same way.
/// </summary>
public static class Forms
{
    /// <summary>A heading; <paramref name="level"/> 2 is a section heading and 3 a sub-heading.</summary>
    public static TextBlock Heading(string text, int level = 2)
    {
        TextBlock heading = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add(level <= 2 ? "h2" : "h3");
        return heading;
    }

    /// <summary>A wrapping paragraph with optional style classes, such as <c>muted</c> or <c>error</c>.</summary>
    public static TextBlock Text(string text, params string[] classes)
    {
        TextBlock paragraph = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        paragraph.Classes.AddRange(classes);
        return paragraph;
    }

    /// <summary>
    /// A library button in <paramref name="variant"/> (<c>primary</c>, <c>secondary</c>, <c>outline</c>, <c>ghost</c>,
    /// <c>destructive</c> or <c>link</c>) that runs <paramref name="click"/>. A failure of the click shows in the nearest
    /// host region.
    /// </summary>
    public static Button Button(string label, string variant, Func<Task> click, ButtonSize size = ButtonSize.Default, bool enabled = true)
    {
        Button button = new() { Content = label, IsEnabled = enabled };
        button.Classes.Add(variant);
        string? sized = size switch
        {
            ButtonSize.Small => "sm",
            ButtonSize.ExtraSmall => "xs",
            ButtonSize.Large => "lg",
            _ => null,
        };
        if (sized is not null) button.Classes.Add(sized);
        button.Click += async (_, _) =>
        {
            try { await click(); }
            catch (Exception error) when (error is not OperationCanceledException) { UiRegion.Report(button, error); }
        };
        return button;
    }

    /// <summary>A library button that runs a synchronous <paramref name="click"/>; see the other overload.</summary>
    public static Button Button(string label, string variant, Action click, ButtonSize size = ButtonSize.Default, bool enabled = true) =>
        Button(label, variant, () =>
        {
            click();
            return Task.CompletedTask;
        }, size, enabled);

    /// <summary>A labelled field: the label above <paramref name="field"/>.</summary>
    public static StackPanel Field(string label, Control field)
    {
        StackPanel stack = new() { Spacing = 6 };
        stack.Classes.Add("field");
        TextBlock name = new() { Text = label };
        name.Classes.Add("label");
        stack.Children.Add(name);
        stack.Children.Add(field);
        return stack;
    }

    /// <summary>
    /// A horizontal row of controls 8 px apart that wraps when it runs out of width. The <c>control-row</c> style centres
    /// each child on its line, so text and controls of one size share a vertical centre.
    /// </summary>
    public static WrapPanel Row(params Control[] children)
    {
        WrapPanel row = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        row.Classes.Add("control-row");
        row.Children.AddRange(children);
        return row;
    }

    /// <summary>One row of a bordered list, such as a granted path or a blocked action: its children side by side.</summary>
    public static Border ListRow(params Control[] children)
    {
        WrapPanel row = Row(children);
        Border box = new() { Child = row };
        box.Classes.Add("list-row");
        return box;
    }

    /// <summary>A vertical stack with <paramref name="spacing"/> between its children.</summary>
    public static StackPanel Stack(double spacing, params Control[] children)
    {
        StackPanel stack = new() { Spacing = spacing };
        stack.Children.AddRange(children);
        return stack;
    }

    /// <summary>A dropdown of <paramref name="options"/> (value and label) showing <paramref name="selected"/>; <paramref name="changed"/> receives the chosen value.</summary>
    public static ComboBox Select(IReadOnlyList<(string Value, string Label)> options, string? selected, Action<string> changed,
        string? placeholder = null)
    {
        ComboBox box = new() { PlaceholderText = placeholder };
        foreach ((string value, string label) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedIndex = options.ToList().FindIndex(option => option.Value == selected);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string value } && value != selected)
            {
                selected = value;
                changed(value);
            }
        };
        return box;
    }
}
