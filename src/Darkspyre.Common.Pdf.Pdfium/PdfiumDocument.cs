using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Versioning;
using Darkspyre.Common.Pdf;

namespace Darkspyre.Common.Pdf.Pdfium;

/// <summary>An open PDFium document with a form-fill environment, over a native copy of the bytes.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal sealed class PdfiumDocument : IPdfDocument
{
    // US Letter, the common default media box, keeps a box for a page whose size cannot be read.
    private const float DefaultWidth = 612, DefaultHeight = 792;
    // A malformed outline can loop, and PDFium then returns the same handles again.
    private const int MaximumOutlineDepth = 64, MaximumOutlineEntries = 10_000;
    // FPDF_PageToDevice returns integers; a 16x device keeps 1/16 point precision.
    private const int DevicePrecision = 16;
    private const int MaximumTile = 1024;

    private readonly nint document, form, data, info;
    private readonly Dictionary<int, Location> bookmarkLocations = [];
    private bool disposed;

    private readonly record struct Location(int Page, bool HasX, float X, bool HasY, float Y);

    /// <summary>Takes ownership of an open document, its form environment and both native buffers. Runs under the library lock.</summary>
    internal PdfiumDocument(nint document, nint form, nint data, nint info)
    {
        this.document = document;
        this.form = form;
        this.data = data;
        this.info = info;
        int count = Pdfium.FPDF_GetPageCount(document);
        var pages = new PdfPage[Math.Max(0, count)];
        for (int index = 0; index < pages.Length; index++)
        {
            bool sized = Pdfium.FPDF_GetPageSizeByIndexF(document, index, out Pdfium.SizeF size) != 0;
            string? label = ReadString((buffer, length) => Pdfium.FPDF_GetPageLabel(document, index, buffer, length));
            pages[index] = new(sized ? size.Width : DefaultWidth, sized ? size.Height : DefaultHeight, label);
        }
        Pages = pages;
        int entries = 0;
        Bookmarks = ReadOutline(0, 0, ref entries);
    }

    /// <inheritdoc />
    public IReadOnlyList<PdfPage> Pages { get; }

    /// <inheritdoc />
    public IReadOnlyList<PdfBookmark> Bookmarks { get; }

    /// <inheritdoc />
    public Task<PdfBitmap?> RenderAsync(int pageIndex, double scale, PdfPixelArea area, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Width is < 1 or > MaximumTile) throw new ArgumentOutOfRangeException(nameof(area), "The area's width must be from 1 to 1024.");
        if (area.Height is < 1 or > MaximumTile) throw new ArgumentOutOfRangeException(nameof(area), "The area's height must be from 1 to 1024.");
        return Pdfium.RunAsync(() => Render(pageIndex, scale, area), cancellationToken);
    }

    private PdfBitmap? Render(int pageIndex, double scale, PdfPixelArea area)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pageIndex < 0 || pageIndex >= Pages.Count) return null;
        nint page = Pdfium.FPDF_LoadPage(document, pageIndex);
        if (page == 0) return null;
        try
        {
            Pdfium.FORM_OnAfterLoadPage(page, form);
            try { return Draw(page, pageIndex, scale, area); }
            finally { Pdfium.FORM_OnBeforeClosePage(page, form); }
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    private unsafe PdfBitmap? Draw(nint page, int pageIndex, double scale, PdfPixelArea area)
    {
        int pageWidth = (int)Math.Round(Pages[pageIndex].Width * scale), pageHeight = (int)Math.Round(Pages[pageIndex].Height * scale);
        byte[] pixels = new byte[area.Width * area.Height * 4];
        fixed (byte* buffer = pixels)
        {
            nint bitmap = Pdfium.FPDFBitmap_CreateEx(area.Width, area.Height, Pdfium.BitmapBgra, (nint)buffer, area.Width * 4);
            if (bitmap == 0) return null;
            try
            {
                if (Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, area.Width, area.Height, new CULong(0xFFFFFFFF)) == 0) return null;
                const int flags = Pdfium.RenderAnnotations | Pdfium.RenderReverseByteOrder;
                Pdfium.FPDF_RenderPageBitmap(bitmap, page, -area.X, -area.Y, pageWidth, pageHeight, 0, flags);
                Pdfium.FPDF_FFLDraw(form, bitmap, page, -area.X, -area.Y, pageWidth, pageHeight, 0, flags);
            }
            finally { Pdfium.FPDFBitmap_Destroy(bitmap); }
        }
        return new(area.Width, area.Height, pixels);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PdfTextLine>> TextAsync(int pageIndex, CancellationToken cancellationToken) =>
        Pdfium.RunAsync<IReadOnlyList<PdfTextLine>>(() => Text(pageIndex), cancellationToken);

    private List<PdfTextLine> Text(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pageIndex < 0 || pageIndex >= Pages.Count) return [];
        nint page = Pdfium.FPDF_LoadPage(document, pageIndex);
        if (page == 0) return [];
        try
        {
            nint text = Pdfium.FPDFText_LoadPage(page);
            if (text == 0) return [];
            try
            {
                var lines = new List<PdfTextLine>();
                var current = new StringBuilder();
                float left = 0, top = 0, right = 0, bottom = 0;
                void Flush()
                {
                    if (current.Length > 0) lines.Add(new(current.ToString(), new(left, top, right - left, bottom - top)));
                    current.Clear();
                }
                int count = Pdfium.FPDFText_CountChars(text);
                for (int index = 0; index < count; index++)
                {
                    uint code = Pdfium.FPDFText_GetUnicode(text, index);
                    if (code is '\r' or '\n') { Flush(); continue; }
                    if (code == 0 || Pdfium.FPDFText_GetLooseCharBox(text, index, out Pdfium.RectF box) == 0) continue;
                    (float x1, float y1) = PointToTop(page, pageIndex, box.Left, box.Top);
                    (float x2, float y2) = PointToTop(page, pageIndex, box.Right, box.Bottom);
                    float charLeft = Math.Min(x1, x2), charTop = Math.Min(y1, y2), charRight = Math.Max(x1, x2), charBottom = Math.Max(y1, y2);
                    if (current.Length > 0 && Math.Abs(charTop - top) > (bottom - top) / 2) Flush();
                    if (current.Length == 0) { left = charLeft; top = charTop; right = charRight; bottom = charBottom; }
                    else { left = Math.Min(left, charLeft); top = Math.Min(top, charTop); right = Math.Max(right, charRight); bottom = Math.Max(bottom, charBottom); }
                    current.Append(code <= 0xFFFF ? ((char)code).ToString() : char.ConvertFromUtf32((int)code));
                }
                Flush();
                return lines;
            }
            finally { Pdfium.FPDFText_ClosePage(text); }
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PdfLink>> LinksAsync(int pageIndex, CancellationToken cancellationToken) =>
        Pdfium.RunAsync<IReadOnlyList<PdfLink>>(() => Links(pageIndex), cancellationToken);

    private List<PdfLink> Links(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pageIndex < 0 || pageIndex >= Pages.Count) return [];
        nint page = Pdfium.FPDF_LoadPage(document, pageIndex);
        if (page == 0) return [];
        try
        {
            var links = new List<PdfLink>();
            int start = 0;
            while (Pdfium.FPDFLink_Enumerate(page, ref start, out nint link) != 0)
            {
                nint destination = Pdfium.FPDFLink_GetDest(document, link);
                if (destination == 0 && Pdfium.FPDFLink_GetAction(link) is var action and not 0
                    && Pdfium.FPDFAction_GetType(action).Value == Pdfium.ActionGoTo)
                    destination = Pdfium.FPDFAction_GetDest(document, action);
                if (destination == 0 || Pdfium.FPDFLink_GetAnnotRect(link, out Pdfium.RectF rect) == 0) continue;
                Location target = LocationOf(destination);
                if (target.Page < 0 || target.Page >= Pages.Count) continue;
                (float x1, float y1) = PointToTop(page, pageIndex, rect.Left, rect.Top);
                (float x2, float y2) = PointToTop(page, pageIndex, rect.Right, rect.Bottom);
                links.Add(new(new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1)), target.Page, ResolveTop(target)));
            }
            return links;
        }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    /// <inheritdoc />
    public Task<float?> BookmarkTopAsync(int bookmarkId, CancellationToken cancellationToken) =>
        Pdfium.RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return bookmarkLocations.TryGetValue(bookmarkId, out Location location) ? ResolveTop(location) : null;
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await Pdfium.RunAsync(() =>
    {
        if (disposed) return true;
        disposed = true;
        Pdfium.FPDFDOC_ExitFormFillEnvironment(form);
        Pdfium.FPDF_CloseDocument(document);
        unsafe
        {
            NativeMemory.Free((void*)info);
            NativeMemory.Free((void*)data);
        }
        return true;
    }, CancellationToken.None).ConfigureAwait(false);

    private List<PdfBookmark> ReadOutline(nint parent, int depth, ref int entries)
    {
        var items = new List<PdfBookmark>();
        if (depth >= MaximumOutlineDepth) return items;
        for (nint bookmark = Pdfium.FPDFBookmark_GetFirstChild(document, parent);
             bookmark != 0 && entries < MaximumOutlineEntries;
             bookmark = Pdfium.FPDFBookmark_GetNextSibling(document, bookmark))
        {
            int id = entries++;
            nint destination = Pdfium.FPDFBookmark_GetDest(document, bookmark);
            if (destination == 0 && Pdfium.FPDFBookmark_GetAction(bookmark) is var action and not 0
                && Pdfium.FPDFAction_GetType(action).Value == Pdfium.ActionGoTo)
                destination = Pdfium.FPDFAction_GetDest(document, action);
            int? page = null;
            if (destination != 0)
            {
                Location location = LocationOf(destination);
                if (location.Page >= 0 && location.Page < Pages.Count) { page = location.Page; bookmarkLocations[id] = location; }
            }
            string title = ReadString((buffer, length) => Pdfium.FPDFBookmark_GetTitle(bookmark, buffer, length)) ?? "";
            items.Add(new(id, title, page, ReadOutline(bookmark, depth + 1, ref entries)));
        }
        return items;
    }

    private Location LocationOf(nint destination)
    {
        int page = Pdfium.FPDFDest_GetDestPageIndex(document, destination);
        return Pdfium.FPDFDest_GetLocationInPage(destination, out int hasX, out int hasY, out _, out float x, out float y, out _) != 0
            ? new(page, hasX != 0, x, hasY != 0, y) : new(page, false, 0, false, 0);
    }

    private float? ResolveTop(Location location)
    {
        if (!location.HasY || location.Page < 0 || location.Page >= Pages.Count) return null;
        nint page = Pdfium.FPDF_LoadPage(document, location.Page);
        if (page == 0) return null;
        try { return PointToTop(page, location.Page, location.HasX ? location.X : 0, location.Y).Y; }
        finally { Pdfium.FPDF_ClosePage(page); }
    }

    // Maps a page-space point to points from the displayed page's top-left, applying the page's rotation and box origin.
    private (float X, float Y) PointToTop(nint page, int pageIndex, double x, double y)
    {
        int width = (int)Math.Round(Pages[pageIndex].Width * DevicePrecision), height = (int)Math.Round(Pages[pageIndex].Height * DevicePrecision);
        Pdfium.FPDF_PageToDevice(page, 0, 0, width, height, 0, x, y, out int deviceX, out int deviceY);
        return ((float)deviceX / DevicePrecision, (float)deviceY / DevicePrecision);
    }

    // Reads a UTF-16LE string through PDFium's two-call pattern: the first call returns the byte length.
    private static unsafe string? ReadString(Func<nint, CULong, CULong> read)
    {
        int length = (int)read(0, new CULong(0)).Value;
        if (length <= 2) return null;
        byte[] bytes = new byte[length];
        fixed (byte* buffer = bytes) read((nint)buffer, new CULong((nuint)length));
        string value = Encoding.Unicode.GetString(bytes, 0, length - 2);
        return value.Length == 0 ? null : value;
    }
}
