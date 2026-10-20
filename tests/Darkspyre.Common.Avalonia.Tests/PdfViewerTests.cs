using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Darkspyre.Agent.Common.UI.Pdf;
using Darkspyre.Common.Pdf;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class PdfViewerTests
{
    [Fact]
    public Task VisiblePagesAreDrawnInTiles() => ViewSession.RunAsync(async () =>
    {
        FakeDocument document = new();
        using PdfViewer viewer = new() { Document = document };
        ViewSession.Show(viewer);

        await ViewSession.WaitForAsync(() => ViewSession.All<Image>(viewer).Any(image => image.Source is WriteableBitmap));
        Assert.Contains(0, document.Rendered);
    });

    [Fact]
    public Task GoToPageMovesTheCurrentPageAndTheStatus() => ViewSession.RunAsync(async () =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument() };
        ViewSession.Show(viewer);

        viewer.GoToPage(2);
        ViewSession.Settle();

        Assert.Equal(2, viewer.CurrentPage);
        await ViewSession.WaitForAsync(() => ViewSession.All<TextBlock>(viewer).Any(block => block.Text == "Page iii (3 of 3)"));
    });

    [Fact]
    public Task ABookmarkGoesToItsPage() => ViewSession.RunAsync(async () =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument() };
        ViewSession.Show(viewer);
        Assert.Single(ViewSession.All<global::Avalonia.Controls.Primitives.ToggleButton>(viewer)).IsChecked = true;
        Assert.Single(ViewSession.All<TabControl>(viewer)).SelectedIndex = 1;
        ViewSession.Settle();
        TreeView outline = Assert.Single(ViewSession.All<TreeView>(viewer));
        TreeViewItem chapter = ((IEnumerable<TreeViewItem>)outline.ItemsSource!).Single();

        outline.SelectedItem = chapter;

        await ViewSession.WaitForAsync(() => viewer.CurrentPage == 1);
    });

    [Fact]
    public Task FindCountsMatchesOnEveryPageAndShowsEachInTurn() => ViewSession.RunAsync(async () =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument() };
        ViewSession.Show(viewer);
        await Task.Delay(50);
        ViewSession.Settle();

        Assert.Equal(3, viewer.Find("needle"));
        Assert.Equal(0, viewer.CurrentPage);
        Assert.Equal(1, viewer.CurrentMatch);
        viewer.FindNext();
        ViewSession.Settle();
        Assert.Equal(1, viewer.CurrentPage);
        viewer.FindNext();
        ViewSession.Settle();
        Assert.Equal(2, viewer.CurrentPage);
        viewer.FindNext();
        ViewSession.Settle();
        Assert.Equal(0, viewer.CurrentPage);
    });

    [Fact]
    public Task AScrollWithUnchangedMatchesRedrawsNoHighlight() => ViewSession.RunAsync(async () =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument() };
        ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => viewer.Find("needle") == 3 && Highlights(viewer).Any());
        Border[] before = [.. Highlights(viewer)];
        ScrollViewer pages = ViewSession.All<ScrollViewer>(viewer)
            .Single(scroller => global::Avalonia.Automation.AutomationProperties.GetName(scroller) == "PDF pages");

        for (int step = 1; step <= 4; step++)
        {
            pages.Offset = new Vector(pages.Offset.X, step * 5);
            ViewSession.Settle();
        }

        Assert.Equal(20, pages.Offset.Y);
        Assert.Equal(before, Highlights(viewer));
    });

    [Fact]
    public Task AResizeThatKeepsTheScaleKeepsTheDrawnTiles() => ViewSession.RunAsync(async () =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument() };
        Window window = ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => Tiles(viewer).Any());
        Image[] before = [.. Tiles(viewer)];
        int percent = viewer.EffectivePercent;

        // Fit width depends on the width only, so a shorter window keeps the scale.
        window.Height = 600;
        ViewSession.Settle();

        Assert.Equal(percent, viewer.EffectivePercent);
        Assert.All(before, tile => Assert.Contains(tile, Tiles(viewer)));
    });

    [Fact]
    public Task ZoomingInStepsThroughThePercentages() => ViewSession.RunAsync(() =>
    {
        using PdfViewer viewer = new() { Document = new FakeDocument(), Zoom = new PdfZoom.Percent(100) };
        ViewSession.Show(viewer);

        Button zoomIn = ViewSession.All<Button>(viewer).Single(button => global::Avalonia.Automation.AutomationProperties.GetName(button) == "Zoom in");
        zoomIn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ViewSession.Settle();

        Assert.Equal(new PdfZoom.Percent(125), viewer.Zoom);
        Assert.Equal(125, viewer.EffectivePercent);
        return Task.CompletedTask;
    });

    // The find highlights sit on the one canvas of each page that ignores the pointer.
    private static IEnumerable<Border> Highlights(PdfViewer viewer) =>
        ViewSession.All<Canvas>(viewer).Where(canvas => !canvas.IsHitTestVisible).SelectMany(canvas => canvas.Children.OfType<Border>());

    private static IEnumerable<Image> Tiles(PdfViewer viewer) => ViewSession.All<Image>(viewer).Where(image => image.Source is WriteableBitmap);

    /// <summary>Three letter-size pages labelled i to iii; each holds "needle" once; page 1 links to page 3; one bookmark to page 2.</summary>
    private sealed class FakeDocument : IPdfDocument
    {
        public List<int> Rendered { get; } = [];

        public IReadOnlyList<PdfPage> Pages { get; } = [new(612, 792, "i"), new(612, 792, "ii"), new(612, 792, "iii")];

        public IReadOnlyList<PdfBookmark> Bookmarks { get; } = [new(7, "Chapter", 1, [])];

        public Task<PdfBitmap?> RenderAsync(int pageIndex, double scale, PdfPixelArea area, CancellationToken cancellationToken)
        {
            lock (this.Rendered) this.Rendered.Add(pageIndex);
            return Task.FromResult<PdfBitmap?>(new(area.Width, area.Height, new byte[area.Width * area.Height * 4]));
        }

        public Task<IReadOnlyList<PdfTextLine>> TextAsync(int pageIndex, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PdfTextLine>>([new($"Page {pageIndex + 1} has a needle in it", new(72, 100, 300, 14))]);

        public Task<IReadOnlyList<PdfLink>> LinksAsync(int pageIndex, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PdfLink>>(pageIndex == 0 ? [new(new(72, 200, 100, 14), 2, null)] : []);

        public Task<float?> BookmarkTopAsync(int bookmarkId, CancellationToken cancellationToken) => Task.FromResult<float?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
