using Darkspyre.Common.Markdown;

namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>Generic attachment icons owned by Darkspyre: one per document classification, and an unavailable image. The artwork is
/// <see cref="AttachmentIconPaths"/>, which the phone's file-kind icon draws too.</summary>
public static class AttachmentPreviewIcons
{
    /// <summary>A framed picture with a sun and hills: an image attachment.</summary>
    public static readonly IconSource Image = IconSource.Fill(AttachmentIconPaths.Image, AttachmentIconPaths.ViewBox);

    /// <summary>An image whose preview could not be made: the image outline struck through.</summary>
    public static readonly IconSource UnavailableImage = IconSource.Fill(AttachmentIconPaths.UnavailableImage, AttachmentIconPaths.ViewBox);

    /// <summary>A page marked PDF: a PDF attachment.</summary>
    public static readonly IconSource Pdf = IconSource.Fill(AttachmentIconPaths.Pdf, AttachmentIconPaths.ViewBox);

    /// <summary>A page with a small table: an Office document attachment.</summary>
    public static readonly IconSource Office = IconSource.Fill(AttachmentIconPaths.Office, AttachmentIconPaths.ViewBox);

    /// <summary>A page with text lines: a text attachment.</summary>
    public static readonly IconSource Text = IconSource.Fill(AttachmentIconPaths.Text, AttachmentIconPaths.ViewBox);

    /// <summary>A blank page with a folded corner: an attachment of any other kind.</summary>
    public static readonly IconSource Other = IconSource.Fill(AttachmentIconPaths.Other, AttachmentIconPaths.ViewBox);
}
