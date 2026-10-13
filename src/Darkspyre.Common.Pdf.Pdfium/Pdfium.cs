using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darkspyre.Common.Pdf.Pdfium;

/// <summary>
/// The PDFium functions File Viewer calls. PDFium is not thread-safe, so every call runs inside <see cref="RunAsync{T}"/>,
/// which holds one lock for this load context's copy of the library.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal static partial class Pdfium
{
    private const string Library = "pdfium";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool initialized;

    /// <summary>
    /// Waits for the lock, initializes the library on first use, and runs work on the thread pool. The library is never
    /// destroyed: destroying it frees only global caches while the native library stays mapped, and it would break any
    /// later engine in the same load context.
    /// </summary>
    internal static async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (!initialized) { FPDF_InitLibrary(); initialized = true; }
                return work();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    internal const uint ErrorPassword = 4;
    internal const int RenderAnnotations = 0x01, RenderReverseByteOrder = 0x10, BitmapBgra = 4;
    internal const uint ActionGoTo = 1;

    /// <summary>
    /// sizeof(FPDF_FORMFILLINFO) on LP64 platforms (64-bit macOS and 64-bit Linux): one int (padded to 8), 15 function
    /// pointers, one pointer (m_pJsPlatform), one FPDF_BOOL (padded to 8) and 17 function pointers = 280 bytes, counted
    /// from fpdf_formfill.h.
    /// </summary>
    internal const int FormFillInfoSize = 280;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeF { public float Width; public float Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF { public float Left; public float Top; public float Right; public float Bottom; }

    [LibraryImport(Library)] internal static partial void FPDF_InitLibrary();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint FPDF_LoadMemDocument64(nint data, nuint size, string? password);
    [LibraryImport(Library)] internal static partial CULong FPDF_GetLastError();
    [LibraryImport(Library)] internal static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Library)] internal static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Library)] internal static partial int FPDF_GetPageSizeByIndexF(nint document, int index, out SizeF size);
    [LibraryImport(Library)] internal static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Library)] internal static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Library)] internal static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Library)] internal static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [LibraryImport(Library)] internal static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);
    [LibraryImport(Library)] internal static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [LibraryImport(Library)] internal static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Library)] internal static partial nint FPDFDOC_InitFormFillEnvironment(nint document, nint formInfo);
    [LibraryImport(Library)] internal static partial void FPDFDOC_ExitFormFillEnvironment(nint form);
    [LibraryImport(Library)] internal static partial void FORM_OnAfterLoadPage(nint page, nint form);
    [LibraryImport(Library)] internal static partial void FORM_OnBeforeClosePage(nint page, nint form);
    [LibraryImport(Library)] internal static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Library)] internal static partial CULong FPDF_GetPageLabel(nint document, int index, nint buffer, CULong length);
    [LibraryImport(Library)] internal static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [LibraryImport(Library)] internal static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [LibraryImport(Library)] internal static partial CULong FPDFBookmark_GetTitle(nint bookmark, nint buffer, CULong length);
    [LibraryImport(Library)] internal static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [LibraryImport(Library)] internal static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Library)] internal static partial CULong FPDFAction_GetType(nint action);
    [LibraryImport(Library)] internal static partial nint FPDFAction_GetDest(nint document, nint action);
    [LibraryImport(Library)] internal static partial int FPDFDest_GetDestPageIndex(nint document, nint destination);
    [LibraryImport(Library)] internal static partial int FPDFDest_GetLocationInPage(nint destination, out int hasX, out int hasY, out int hasZoom, out float x, out float y, out float zoom);
    [LibraryImport(Library)] internal static partial int FPDFLink_Enumerate(nint page, ref int start, out nint link);
    [LibraryImport(Library)] internal static partial nint FPDFLink_GetDest(nint document, nint link);
    [LibraryImport(Library)] internal static partial nint FPDFLink_GetAction(nint link);
    [LibraryImport(Library)] internal static partial int FPDFLink_GetAnnotRect(nint link, out RectF rect);
    [LibraryImport(Library)] internal static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Library)] internal static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Library)] internal static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Library)] internal static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Library)] internal static partial int FPDFText_GetLooseCharBox(nint textPage, int index, out RectF rect);
}
