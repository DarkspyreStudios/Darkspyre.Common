using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// The parts of a side panel in the Git panel's look, with the <c>panel-*</c> classes <c>ControlStyles</c> styles. Each
/// part's text follows its typography section's entries: names, rows, selectors, boxes and text buttons the Titles entry,
/// details, notes, status lines and in-row buttons the Secondary entry, section titles, counts and tags the Small entry.
/// </summary>
public static class PanelParts
{
    /// <summary>A <c>DockPanel.panel-section</c>: the title in capitals at the left and the count at the right.</summary>
    /// <param name="title">The header's title; <see cref="ShowSection"/> sets its text.</param>
    /// <param name="count">The header's count; <see cref="ShowSection"/> sets its text.</param>
    public static DockPanel Section(out TextBlock title, out TextBlock count)
    {
        DockPanel header = Header(out title, out count);
        header.Classes.Add("panel-section");
        return header;
    }

    /// <summary>Sets a header's title (in capitals), count and automation name, such as "Staged Changes, 2".</summary>
    /// <param name="header">The header control that carries the automation name.</param>
    /// <param name="title">The header's title.</param>
    /// <param name="count">The header's count, empty when <paramref name="number"/> is null.</param>
    /// <param name="text">The section's name as it reads, before capitals.</param>
    /// <param name="number">The count to show, or null for none.</param>
    public static void ShowSection(Control header, TextBlock title, TextBlock count, string text, int? number)
    {
        title.Text = text.ToUpperInvariant();
        count.Text = number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        AutomationProperties.SetName(header, number is { } shown
            ? string.Create(CultureInfo.InvariantCulture, $"{text}, {shown}") : text);
    }

    /// <summary>
    /// An <c>Expander.panel-section</c>. Its header is a bare DockPanel, without the panel-section class or margin, holding a
    /// panel-section-title (the text in capitals) and a panel-count docked right; the header's own padding insets it.
    /// </summary>
    /// <param name="text">The section's name as it reads, before capitals.</param>
    /// <param name="content">What the section shows while open.</param>
    /// <param name="open">Whether the section starts open.</param>
    /// <param name="plainWhenOpen">True adds the class plain-when-open, which hides the chevron while the section is open.</param>
    /// <param name="count">The header's count, empty until the caller sets it.</param>
    public static Expander CollapsibleSection(string text, Control content, bool open, bool plainWhenOpen, out TextBlock count)
    {
        DockPanel header = Header(out TextBlock title, out count);
        title.Text = text.ToUpperInvariant();
        Expander section = new() { Header = header, Content = content, IsExpanded = open };
        section.Classes.Add("panel-section");
        if (plainWhenOpen) section.Classes.Add("plain-when-open");
        AutomationProperties.SetName(section, text);
        return section;
    }

    /// <summary>A full-width <c>Button.panel-row</c> whose content the caller lays out: a row the person clicks.</summary>
    public static Button Row()
    {
        Button row = new();
        row.Classes.Add("panel-row");
        return row;
    }

    /// <summary>
    /// A <c>DockPanel.panel-flat-row</c>: <paramref name="content"/> fills it and <paramref name="actions"/> sit docked right,
    /// 6 px apart, in order: a row that is not clicked as a whole.
    /// </summary>
    /// <param name="content">The row's text or layout.</param>
    /// <param name="actions">The row's buttons, such as <see cref="RowButton"/>s.</param>
    public static DockPanel FlatRow(Control content, params Control[] actions)
    {
        DockPanel row = new();
        row.Classes.Add("panel-flat-row");
        if (actions.Length > 0)
        {
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.AddRange(actions);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
        }
        row.Children.Add(content);
        return row;
    }

    /// <summary>
    /// A <c>Button.panel-button</c>: compact, 24 px at the default size and taller with its text; primary and destructive
    /// keep their fill, every other variant is drawn as secondary.
    /// </summary>
    /// <gotcha>Its height follows its text, so a row of these grows when the Tools title entry is raised.</gotcha>
    /// <param name="label">The button's text.</param>
    /// <param name="variant">A <see cref="Forms.Button(string, string, Func{Task}, ButtonSize, bool)"/> variant.</param>
    /// <param name="click">Runs on each click; a failure shows in the nearest host region.</param>
    public static Button TextButton(string label, string variant, Func<Task> click)
    {
        Button button = Forms.Button(label, variant is "primary" or "destructive" ? variant : "secondary", click, ButtonSize.ExtraSmall);
        button.Classes.Add("panel-button");
        return button;
    }

    /// <summary>A secondary <c>Button.panel-button.in-row</c>: the smaller button inside a row, such as Stop, Edit, Remove or Log.</summary>
    /// <param name="label">The button's text.</param>
    /// <param name="click">Runs on each click; a failure shows in the nearest host region.</param>
    public static Button RowButton(string label, Func<Task> click)
    {
        Button button = TextButton(label, "secondary", click);
        button.Classes.Add("in-row");
        return button;
    }

    /// <summary>A 22 px <c>Button.panel-icon</c> showing <paramref name="icon"/>, named and explained by <paramref name="name"/>.</summary>
    /// <param name="icon">The glyph.</param>
    /// <param name="name">The button's tooltip and automation name.</param>
    /// <param name="click">Runs on each click.</param>
    /// <param name="size">The glyph's width and height in pixels.</param>
    /// <param name="angle">The glyph's rotation in degrees.</param>
    public static Button IconButton(IconSource icon, string name, Action click, double size = 14, double angle = 0)
    {
        Icon glyph = new() { Source = icon, Width = size, Height = size };
        if (angle != 0) glyph.RenderTransform = new RotateTransform(angle);
        Button button = new() { Content = glyph };
        button.Classes.Add("panel-icon");
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A <c>TextBlock.panel-note</c>: the message that stands in for a view.</summary>
    public static TextBlock Note(string text)
    {
        TextBlock note = new() { Text = text };
        note.Classes.Add("panel-note");
        return note;
    }

    /// <summary>A <c>TextBlock.panel-status</c>: an inline message line; <paramref name="error"/> adds the error class.</summary>
    public static TextBlock Status(string text, bool error = false)
    {
        TextBlock status = new() { Text = text };
        status.Classes.Add("panel-status");
        if (error) status.Classes.Add("error");
        return status;
    }

    /// <summary>A row's dimmed <c>TextBlock.panel-detail</c>, docked right.</summary>
    /// <param name="classes">More style classes, such as <c>wrap</c> for a second line under a row.</param>
    public static TextBlock Detail(params string[] classes)
    {
        TextBlock detail = new();
        detail.Classes.Add("panel-detail");
        detail.Classes.AddRange(classes);
        DockPanel.SetDock(detail, Dock.Right);
        return detail;
    }

    /// <summary>A row's <c>TextBlock.panel-name</c>, which fills the row and trims.</summary>
    public static TextBlock Name()
    {
        TextBlock name = new() { Margin = new Thickness(0, 0, 8, 0) };
        name.Classes.Add("panel-name");
        return name;
    }

    /// <summary>A <c>Border.panel-tag</c> holding <paramref name="text"/>; <paramref name="tone"/> is null, "ok", "warn" or "danger".</summary>
    public static Border Tag(string text, string? tone = null)
    {
        Border tag = new() { Child = new TextBlock { Text = text } };
        tag.Classes.Add("panel-tag");
        if (tone is not null) tag.Classes.Add(tone);
        return tag;
    }

    /// <summary>A <c>Border.panel-bar</c> holding <paramref name="child"/>.</summary>
    public static Border Bar(Control child)
    {
        Border bar = new() { Child = child };
        bar.Classes.Add("panel-bar");
        return bar;
    }

    // A section header's title and count, docked as the panel-section header lays them out.
    private static DockPanel Header(out TextBlock title, out TextBlock count)
    {
        DockPanel header = new();
        count = new TextBlock();
        count.Classes.Add("panel-count");
        DockPanel.SetDock(count, Dock.Right);
        title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        title.Classes.Add("panel-section-title");
        header.Children.Add(count);
        header.Children.Add(title);
        return header;
    }
}
