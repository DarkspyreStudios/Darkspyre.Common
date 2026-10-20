namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>Generic attachment icons owned by Darkspyre: one per document classification, and an unavailable image.</summary>
public static class AttachmentPreviewIcons
{
    /// <summary>A framed picture with a sun and hills: an image attachment.</summary>
    public static readonly IconSource Image = IconSource.Fill("""
        <rect x="1.5" y="2.5" width="13" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <circle cx="5.5" cy="6" r="1.2" fill="currentColor"/>
        <path d="M2 12l4-4 3 3 2-2 3.5 3.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>An image whose preview could not be made: the image outline struck through.</summary>
    public static readonly IconSource UnavailableImage = IconSource.Fill("""
        <rect x="1.5" y="2.5" width="13" height="11" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/>
        <path d="M2 14L14 2" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
        """, "0 0 16 16");

    /// <summary>A page marked PDF: a PDF attachment.</summary>
    public static readonly IconSource Pdf = IconSource.Fill("""
        <path d="M3 1.5h6l4 4v9H3zM9 1.5v4h4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <text x="8" y="12.2" font-size="4.2" font-family="sans-serif" font-weight="700" text-anchor="middle" fill="currentColor">PDF</text>
        """, "0 0 16 16");

    /// <summary>A page with a small table: an Office document attachment.</summary>
    public static readonly IconSource Office = IconSource.Fill("""
        <path d="M3 1.5h6l4 4v9H3zM9 1.5v4h4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <rect x="5" y="8" width="6" height="4.5" fill="none" stroke="currentColor" stroke-width="1"/>
        <path d="M5 10.25h6M8 8v4.5" fill="none" stroke="currentColor" stroke-width="1"/>
        """, "0 0 16 16");

    /// <summary>A page with text lines: a text attachment.</summary>
    public static readonly IconSource Text = IconSource.Fill("""
        <path d="M3 1.5h6l4 4v9H3zM9 1.5v4h4M5.5 8h5M5.5 10.5h5M5.5 13h3" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");

    /// <summary>A blank page with a folded corner: an attachment of any other kind.</summary>
    public static readonly IconSource Other = IconSource.Fill("""
        <path d="M3 1.5h6l4 4v9H3zM9 1.5v4h4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        """, "0 0 16 16");
}
