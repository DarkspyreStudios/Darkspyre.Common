using Darkspyre.Common.Markdown;
using Microsoft.AspNetCore.Components;

namespace Darkspyre.Mobile.Components.Icons;

/// <summary>The kind of file an icon stands for.</summary>
public enum FileKind
{
    /// <summary>A picture.</summary>
    Image,

    /// <summary>A PDF document.</summary>
    Pdf,

    /// <summary>An Office document.</summary>
    Office,

    /// <summary>A text file.</summary>
    Text,

    /// <summary>Any other file.</summary>
    Other,
}

/// <summary>
/// The icon of a file kind, drawn inline as SVG in the current text colour from <see cref="AttachmentIconPaths"/>, the artwork the
/// desktop's attachment icons draw. It is sized by the <c>icon</c> class of mobile.css.
/// </summary>
/// <remarks>The artwork is constant markup owned by Darkspyre.Common.Markdown; no file name or other input reaches it.</remarks>
public sealed partial class FileKindIcon : IconBase
{
    /// <summary>The kind of file the icon shows.</summary>
    [Parameter]
    public FileKind Kind { get; set; } = FileKind.Other;

    private string Artwork => this.Kind switch
    {
        FileKind.Image => AttachmentIconPaths.Image,
        FileKind.Pdf => AttachmentIconPaths.Pdf,
        FileKind.Office => AttachmentIconPaths.Office,
        FileKind.Text => AttachmentIconPaths.Text,
        _ => AttachmentIconPaths.Other,
    };
}
