using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>The look of a <see cref="Badge"/>.</summary>
public enum BadgeVariant
{
    /// <summary>The primary colour.</summary>
    Default,

    /// <summary>The secondary surface.</summary>
    Secondary,

    /// <summary>An outline on no fill.</summary>
    Outline,

    /// <summary>The destructive colour.</summary>
    Destructive,
}

/// <summary>A short label in a small rounded chip: 12 px medium text, 2 px by 8 px padding.</summary>
public sealed class Badge : ContentControl
{
    /// <summary>Creates a badge showing <paramref name="text"/> in <paramref name="variant"/>.</summary>
    public Badge(string text, BadgeVariant variant = BadgeVariant.Default)
    {
        this.Content = text;
        this.Classes.Add("badge");
        this.Classes.Add(variant.ToString().ToLowerInvariant());
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ContentControl);
}
