using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Darkspyre.Agent.Common.UI.Themes;

/// <summary>The common controls in the shell's look, read from the theme resources.</summary>
public sealed class ControlStyles : Styles
{
    /// <summary>Loads the styles.</summary>
    public ControlStyles() => AvaloniaXamlLoader.Load(this);
}
