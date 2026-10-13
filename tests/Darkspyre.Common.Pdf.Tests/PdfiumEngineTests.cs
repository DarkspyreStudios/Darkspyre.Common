using System.Runtime.Versioning;
using Darkspyre.Common.Pdf.Pdfium;
using Xunit;

namespace Darkspyre.Common.Pdf.Tests;

/// <summary>Open, inspect, render, text, bookmarks and links through the real PDFium engine.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
public sealed class PdfiumEngineTests
{
    private static async Task<IPdfDocument> OpenAsync(string fixture, string? password = null)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        PdfOpenResult result = await new PdfiumEngine().OpenAsync(bytes, password, default);
        Assert.Equal(PdfOpenStatus.Opened, result.Status);
        return result.Document!;
    }

    [Fact]
    public async Task OpensAndListsThreePagesWithSizes()
    {
        await using IPdfDocument document = await OpenAsync("three-pages.pdf");
        Assert.Equal(["i", "ii", "1"], document.Pages.Select(page => page.Label));
        Assert.All(document.Pages, page => Assert.Equal((612f, 792f), (page.Width, page.Height)));
    }

    [Fact]
    public async Task APasswordFileReportsPasswordRequiredThenOpensWithIt()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages-protected.pdf"));
        var engine = new PdfiumEngine();
        PdfOpenResult required = await engine.OpenAsync(bytes, null, default);
        Assert.Equal((PdfOpenStatus.PasswordRequired, (IPdfDocument?)null), (required.Status, required.Document));
        PdfOpenResult incorrect = await engine.OpenAsync(bytes, "wrong", default);
        Assert.Equal((PdfOpenStatus.IncorrectPassword, (IPdfDocument?)null), (incorrect.Status, incorrect.Document));
        await using IPdfDocument document = await OpenAsync("three-pages-protected.pdf", "secret");
        Assert.Equal(3, document.Pages.Count);
    }

    [Fact]
    public async Task RendersATileWithinTheLimitAndRefusesALargerOne()
    {
        await using IPdfDocument document = await OpenAsync("three-pages.pdf");
        PdfBitmap bitmap = (await document.RenderAsync(1, 2, new(0, 0, 1024, 200), default))!;
        Assert.Equal((1024, 200, 1024 * 200 * 4), (bitmap.Width, bitmap.Height, bitmap.Rgba.Length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderAsync(1, 2, new(0, 0, 1025, 200), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderAsync(1, 2, new(0, 0, 200, 1025), default));
    }

    [Fact]
    public async Task ReadsTextLinesWithBoxesInsideThePage()
    {
        await using IPdfDocument document = await OpenAsync("three-pages.pdf");
        IReadOnlyList<PdfTextLine> lines = await document.TextAsync(0, default);
        int alpha = lines.ToList().FindIndex(line => line.Text.Trim() == "Alpha margin");
        int beta = lines.ToList().FindIndex(line => line.Text.Trim() == "Beta line");
        Assert.True(alpha >= 0 && beta > alpha);
        Assert.True(lines[alpha].Box.Top < lines[beta].Box.Top);
        PdfPage page = document.Pages[0];
        Assert.All(lines, line => Assert.True(line.Box.Left >= 0 && line.Box.Top >= 0 && line.Box.Width > 0 && line.Box.Height > 0
            && line.Box.Left + line.Box.Width <= page.Width && line.Box.Top + line.Box.Height <= page.Height));
    }

    [Fact]
    public async Task ReadsBookmarksAndInternalLinks()
    {
        await using IPdfDocument document = await OpenAsync("three-pages.pdf");
        PdfBookmark part = Assert.Single(document.Bookmarks);
        Assert.Equal(("Part", 0), (part.Title, part.Page!.Value));
        PdfBookmark section = Assert.Single(part.Children);
        Assert.Equal(("Section", 2), (section.Title, section.Page!.Value));
        PdfLink link = Assert.Single(await document.LinksAsync(0, default));
        Assert.Equal(2, link.TargetPage);
    }
}
