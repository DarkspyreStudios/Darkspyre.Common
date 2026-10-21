using Microsoft.AspNetCore.Components;

namespace Darkspyre.Mobile.Components.Icons;

/// <summary>A Lucide icon drawn inline as SVG in the current text colour, sized by the <c>icon</c> class of mobile.css.</summary>
/// <remarks>
/// The artwork is Lucide (https://lucide.dev), under the ISC licence, and the icons Lucide derives from Feather are also
/// under the MIT licence. THIRD-PARTY-NOTICES.md at the repository root holds both notices.
/// </remarks>
public abstract class IconBase : ComponentBase
{
    /// <summary>More CSS classes for the svg element, after <c>icon</c>.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>The svg element's class attribute.</summary>
    protected string CssClass => string.IsNullOrEmpty(this.Class) ? "icon" : "icon " + this.Class;
}
