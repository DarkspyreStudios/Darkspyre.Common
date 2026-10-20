using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>
/// Chooses one icon from a named catalog: a button showing the chosen icon and its name opens a searchable grid, with
/// Clear to choose none.
/// </summary>
public sealed class IconPicker : UserControl
{
    private readonly IReadOnlyDictionary<string, IconSource> catalog;
    private readonly Action<string?> changed;
    private readonly Button trigger = new() { HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Flyout flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly WrapPanel grid = new() { MaxWidth = 320 };
    private readonly TextBox search = new() { PlaceholderText = "Search icons" };
    private string? value;

    /// <summary>Creates the picker over <paramref name="catalog"/> showing <paramref name="value"/>; <paramref name="changed"/> receives a choice.</summary>
    public IconPicker(IReadOnlyDictionary<string, IconSource> catalog, string? value, Action<string?> changed)
    {
        this.catalog = catalog;
        this.changed = changed;
        this.value = value;
        this.trigger.Classes.Add("outline");
        this.trigger.Classes.Add("icon-picker-trigger");
        this.trigger.Flyout = this.flyout;
        this.search.TextChanged += (_, _) => this.Fill();
        ScrollViewer scroll = new() { Content = this.grid, MaxHeight = 280, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Button clear = Forms.Button("Clear", "ghost", () => this.Choose(null), ButtonSize.Small);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        this.flyout.Content = Forms.Stack(8, this.search, scroll, clear);
        this.flyout.Opened += (_, _) => this.Fill();
        this.Content = this.trigger;
        this.ShowValue();
    }

    /// <summary>Shows <paramref name="value"/> in place; an unchanged value changes nothing.</summary>
    public string? Value
    {
        set
        {
            if (value == this.value) return;
            this.value = value;
            this.ShowValue();
        }
    }

    private void ShowValue()
    {
        StackPanel shown = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (this.value is not null && this.catalog.TryGetValue(this.value, out IconSource? icon)) shown.Children.Add(new Icon { Source = icon });
        shown.Children.Add(new TextBlock { Text = this.value ?? "Choose an icon", VerticalAlignment = VerticalAlignment.Center });
        this.trigger.Content = shown;
    }

    // The grid holds the icons whose name contains the search text.
    private void Fill()
    {
        string filter = this.search.Text?.Trim() ?? string.Empty;
        this.grid.Children.Clear();
        foreach ((string name, IconSource icon) in this.catalog)
        {
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            ToggleButton choice = new() { Content = new Icon { Source = icon }, IsChecked = name == this.value, Width = 32, Height = 32 };
            choice.Classes.Add("icon-picker-choice");
            ToolTip.SetTip(choice, name);
            Avalonia.Automation.AutomationProperties.SetName(choice, name);
            string chosen = name;
            choice.Click += (_, _) => this.Choose(chosen);
            this.grid.Children.Add(choice);
        }
    }

    private void Choose(string? chosen)
    {
        this.value = chosen;
        this.flyout.Hide();
        this.ShowValue();
        this.changed(chosen);
    }
}

/// <summary>
/// Chooses a colour as <c>#RRGGBB</c>: preset swatches, a hex field and a full colour view in a flyout. The field
/// accepts anything typed; the owner receives the text as typed and validates it.
/// </summary>
public sealed class ColorPicker : UserControl
{
    private readonly TextBox hex = new() { PlaceholderText = "#RRGGBB", Width = 104 };
    private readonly Border swatch = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly Action<string?> changed;
    private bool updating;
    // The text the picker last put in the field. Avalonia raises TextChanged for a programmatic write after the write
    // returns, from a later dispatcher job; that event carries this text and is not the person's edit.
    private string? shown;

    /// <summary>Creates the picker showing <paramref name="value"/> with <paramref name="presets"/> to choose from.</summary>
    public ColorPicker(string? value, IReadOnlyList<string> presets, Action<string?> changed)
    {
        this.changed = changed;
        this.swatch.Classes.Add("color-picker-swatch");
        ColorView view = new() { IsAlphaEnabled = false, IsAlphaVisible = false };
        if (value is not null && Color.TryParse(value, out Color initial)) view.Color = initial;
        view.ColorChanged += (_, args) =>
        {
            if (this.updating) return;
            this.Set(string.Create(CultureInfo.InvariantCulture, $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}"));
        };
        Button custom = new() { Content = this.swatch, Flyout = new Flyout { Content = view }, Padding = new Thickness(0) };
        custom.Classes.Add("color-picker-custom");
        ToolTip.SetTip(custom, "Choose a colour");
        WrapPanel row = Forms.Row(custom, this.hex);
        foreach (string preset in presets)
        {
            Button chip = new() { Width = 20, Height = 20, Padding = new Thickness(0), Background = Brush.Parse(preset), CornerRadius = new CornerRadius(10) };
            chip.Classes.Add("color-picker-preset");
            ToolTip.SetTip(chip, preset);
            Avalonia.Automation.AutomationProperties.SetName(chip, preset);
            string chosen = preset;
            chip.Click += (_, _) => this.Set(chosen);
            row.Children.Add(chip);
        }
        this.hex.TextChanged += (_, _) =>
        {
            if (this.updating || this.hex.Text == this.shown) return;
            this.shown = this.hex.Text;
            this.ShowSwatch(this.hex.Text);
            this.changed(string.IsNullOrWhiteSpace(this.hex.Text) ? null : this.hex.Text.Trim());
        };
        this.Content = row;
        this.Show(value);
    }

    /// <summary>Shows <paramref name="value"/> in place without reporting it; an unchanged value changes nothing.</summary>
    public string? Value
    {
        set
        {
            if (value != this.shown) this.Show(value);
        }
    }

    private void Set(string colour)
    {
        this.Show(colour);
        this.changed(colour);
    }

    private void Show(string? colour)
    {
        this.updating = true;
        this.shown = colour;
        this.hex.Text = colour;
        this.updating = false;
        this.ShowSwatch(colour);
    }

    private void ShowSwatch(string? colour) =>
        this.swatch.Background = colour is not null && Color.TryParse(colour, out Color parsed) ? new SolidColorBrush(parsed) : Brushes.Transparent;
}
