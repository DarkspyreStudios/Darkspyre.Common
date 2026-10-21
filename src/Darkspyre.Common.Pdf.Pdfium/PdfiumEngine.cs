using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Darkspyre.Common.Pdf;

namespace Darkspyre.Common.Pdf.Pdfium;

/// <summary>The PDFium engine.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class PdfiumEngine : IPdfEngine
{
    /// <summary>Always true.</summary>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<PdfOpenResult> OpenAsync(ReadOnlyMemory<byte> bytes, string? password, CancellationToken cancellationToken) =>
        Pdfium.RunAsync(() => Open(bytes, password), cancellationToken);

    private static unsafe PdfOpenResult Open(ReadOnlyMemory<byte> bytes, string? password)
    {
        // PDFium reads from this buffer for the document's life, so it stays allocated until the document is disposed.
        nint data = (nint)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.Span.CopyTo(new Span<byte>((void*)data, bytes.Length));
        nint document = Pdfium.FPDF_LoadMemDocument64(data, (nuint)bytes.Length, password);
        if (document == 0)
        {
            uint error = (uint)Pdfium.FPDF_GetLastError().Value;
            NativeMemory.Free((void*)data);
            if (error == Pdfium.ErrorPassword)
                return new(password is null ? PdfOpenStatus.PasswordRequired : PdfOpenStatus.IncorrectPassword, null);
            throw new IOException("This file could not be opened as a PDF.");
        }
        // Every callback stays null, so PDFium holds no reference to managed code. The environment exists only so
        // form fields are drawn; no input call is ever made.
        nint info = (nint)NativeMemory.AllocZeroed(Pdfium.FormFillInfoSize);
        *(int*)info = 1;
        nint form = Pdfium.FPDFDOC_InitFormFillEnvironment(document, info);
        return new(PdfOpenStatus.Opened, new PdfiumDocument(document, form, data, info));
    }
}
