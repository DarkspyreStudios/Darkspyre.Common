using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>
/// The round Scroll to newest button over a transcript. It shows a down arrow, or, while the conversation works, three
/// dots that rise and fall in turn. It has no word label; its accessible name and tooltip are <see cref="Label"/>.
/// </summary>
internal sealed class NewestButton : Button
{
    /// <summary>The button's accessible name and tooltip.</summary>
    internal const string Label = "Scroll to newest";

    /// <summary>Creates the button; <paramref name="click"/> runs each time the person presses it.</summary>
    internal NewestButton(Action click)
    {
        this.Classes.Add("outline");
        this.Classes.Add("newest");
        Icon arrow = new() { Source = AppIcons.ArrowDown, Width = 16, Height = 16 };
        arrow.Classes.Add("newest-arrow");
        StackPanel dots = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
        dots.Classes.Add("newest-dots");
        foreach (string place in (string[])["first", "second", "third"])
        {
            Ellipse dot = new() { Width = 4, Height = 4, RenderTransform = new TranslateTransform() };
            dot.Classes.Add("newest-dot");
            dot.Classes.Add(place);
            dot.Bind(Shape.FillProperty, dots.GetObservable(TextElement.ForegroundProperty));
            dots.Children.Add(dot);
        }
        Grid face = new();
        face.Children.Add(arrow);
        face.Children.Add(dots);
        this.Content = face;
        this.Click += (_, _) => click();
        AutomationProperties.SetName(this, Label);
        ToolTip.SetTip(this, Label);
    }

    /// <summary>Whether the conversation works now. While it does, the dots show in place of the arrow.</summary>
    internal bool Working
    {
        get => this.Classes.Contains("working");
        set => this.Classes.Set("working", value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);
}
