namespace Darkspyre.Common.Pdf;

// Every coordinate in this seam is in points from the top-left of the page as displayed, after the page's rotation.
// Pixels are 8-bit RGBA, top row first, with no row padding. These conventions belong to the seam, not to an engine.

/// <summary>Opens PDF bytes. Stateless apart from the documents it opens; instances may be shared.</summary>
public interface IPdfEngine
{
    /// <summary>False when this build has no PDF engine. OpenAsync must not be called then.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Opens a copy of the bytes. <see cref="PdfOpenStatus.PasswordRequired"/> when the file needs a password and none
    /// was given; <see cref="PdfOpenStatus.IncorrectPassword"/> when the given one fails.
    /// </summary>
    /// <exception cref="IOException">The bytes are not a PDF the engine can open.</exception>
    /// <exception cref="PlatformNotSupportedException">The engine is unavailable.</exception>
    Task<PdfOpenResult> OpenAsync(ReadOnlyMemory<byte> bytes, string? password, CancellationToken cancellationToken);
}

/// <summary>Whether an open succeeded or needs a password.</summary>
public enum PdfOpenStatus
{
    /// <summary>The document is open.</summary>
    Opened,
    /// <summary>The file needs a password and none was given.</summary>
    PasswordRequired,
    /// <summary>The given password does not open the file.</summary>
    IncorrectPassword,
}

/// <summary>The outcome of an open. Document is set only when Status is Opened; the caller owns and disposes it.</summary>
/// <param name="Status">Whether the open succeeded or needs a password.</param>
/// <param name="Document">The open document when Status is Opened; otherwise null.</param>
public sealed record PdfOpenResult(PdfOpenStatus Status, IPdfDocument? Document);

/// <summary>One open, read-only PDF.</summary>
/// <gotcha>Every member except Pages and Bookmarks throws ObjectDisposedException after DisposeAsync.</gotcha>
public interface IPdfDocument : IAsyncDisposable
{
    /// <summary>Every page in order.</summary>
    IReadOnlyList<PdfPage> Pages { get; }

    /// <summary>The outline as a tree, read at open.</summary>
    IReadOnlyList<PdfBookmark> Bookmarks { get; }

    /// <summary>
    /// Draws an area of a page with its comments, stamps and form fields, at a scale in pixels per point. Null when
    /// the page cannot be drawn.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The area's width or height is outside 1 to 1024.</exception>
    Task<PdfBitmap?> RenderAsync(int pageIndex, double scale, PdfPixelArea area, CancellationToken cancellationToken);

    /// <summary>The page's text as lines in reading order. Empty when the page has no text or cannot be read.</summary>
    Task<IReadOnlyList<PdfTextLine>> TextAsync(int pageIndex, CancellationToken cancellationToken);

    /// <summary>The page's links to pages of this document. Links to anything else are left out.</summary>
    Task<IReadOnlyList<PdfLink>> LinksAsync(int pageIndex, CancellationToken cancellationToken);

    /// <summary>
    /// The distance in points from the top of its target page to a bookmark's destination, or null when the
    /// destination has none.
    /// </summary>
    Task<float?> BookmarkTopAsync(int bookmarkId, CancellationToken cancellationToken);
}

/// <summary>A page's displayed size in points and its label, or null when it has none.</summary>
/// <param name="Width">The displayed width in points.</param>
/// <param name="Height">The displayed height in points.</param>
/// <param name="Label">The page label, or null when it has none.</param>
public sealed record PdfPage(float Width, float Height, string? Label);

/// <summary>An outline entry. Id is unique within the document. Page is the 0-based target page, or null.</summary>
/// <param name="Id">The entry's ID, unique within the document.</param>
/// <param name="Title">The entry's title.</param>
/// <param name="Page">The 0-based target page, or null.</param>
/// <param name="Children">The nested entries.</param>
public sealed record PdfBookmark(int Id, string Title, int? Page, IReadOnlyList<PdfBookmark> Children);

/// <summary>A rectangle in points from the displayed page's top-left.</summary>
/// <param name="Left">The distance from the page's left edge in points.</param>
/// <param name="Top">The distance from the page's top edge in points.</param>
/// <param name="Width">The width in points.</param>
/// <param name="Height">The height in points.</param>
public sealed record PdfRect(float Left, float Top, float Width, float Height);

/// <summary>One line of page text and its box.</summary>
/// <param name="Text">The line's text.</param>
/// <param name="Box">The line's box.</param>
public sealed record PdfTextLine(string Text, PdfRect Box);

/// <summary>A link area and its target: a 0-based page and the destination's distance from that page's top, or null.</summary>
/// <param name="Box">The link area.</param>
/// <param name="TargetPage">The 0-based target page.</param>
/// <param name="TargetTop">The destination's distance in points from the target page's top, or null.</param>
public sealed record PdfLink(PdfRect Box, int TargetPage, float? TargetTop);

/// <summary>An area in pixels of the page drawn at the requested scale, from its top-left.</summary>
/// <param name="X">The left edge in pixels.</param>
/// <param name="Y">The top edge in pixels.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public sealed record PdfPixelArea(int X, int Y, int Width, int Height);

/// <summary>RGBA pixels, Width * 4 bytes per row.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Rgba">The pixels, Width * 4 bytes per row.</param>
public sealed record PdfBitmap(int Width, int Height, byte[] Rgba);

/// <summary>The engine of a build with no PDF implementation.</summary>
public sealed class UnavailablePdfEngine : IPdfEngine
{
    /// <summary>The message a PDF open reports when the build has no PDF engine.</summary>
    public const string Message = "PDF preview is not available on this operating system.";

    /// <summary>Always false.</summary>
    public bool IsAvailable => false;

    /// <summary>Always throws <see cref="PlatformNotSupportedException"/> with <see cref="Message"/>.</summary>
    public Task<PdfOpenResult> OpenAsync(ReadOnlyMemory<byte> bytes, string? password, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(Message);
}
