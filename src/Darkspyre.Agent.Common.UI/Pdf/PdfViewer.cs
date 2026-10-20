using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Darkspyre.Common.Pdf;

namespace Darkspyre.Agent.Common.UI.Pdf;

/// <summary>Shows an open PDF: pages, selectable text, internal links, bookmarks, thumbnails, zoom and find.</summary>
/// <remarks>
/// <para>
/// Pages are laid out at their displayed size; only pages near the visible area hold drawn tiles, text and links, so a
/// long document costs little more than a short one. Each page is drawn in tiles of at most 1024 by 1024 device
/// pixels through <see cref="IPdfDocument.RenderAsync"/>, at most two at a time, nearest pages first. A zoom change
/// drops the tiles drawn at the old size; a resize that leaves the scale as it was keeps them. Find highlights are
/// redrawn only when the matches, the shown match or a page's text layer change, not on every scroll step.
/// </para>
/// <para>
/// The viewer reads every page's text once, in the background, for selection and find. A page whose text cannot be
/// read shows "PDF page text could not be read." above the pages; a page that cannot be drawn shows "This page could
/// not be displayed." on the page. The caller opens the document, password included, and disposes it after the viewer.
/// </para>
/// </remarks>
public sealed class PdfViewer : TemplatedControl, IDisposable
{
    /// <summary>The document shown.</summary>
    public static readonly StyledProperty<IPdfDocument?> DocumentProperty = AvaloniaProperty.Register<PdfViewer, IPdfDocument?>(nameof(Document));

    /// <summary>The zoom the pages are drawn at.</summary>
    public static readonly StyledProperty<PdfZoom> ZoomProperty = AvaloniaProperty.Register<PdfViewer, PdfZoom>(nameof(Zoom), new PdfZoom.FitWidth());

    // Device-independent pixels per point at 100%: one point is 1/72 inch and one pixel 1/96 inch.
    private const double PixelsPerPoint = 96d / 72d;
    // Device pixels per tile side; bounds one tile's buffer at 4 MiB.
    private const int TileSize = 1024;
    // Tiles drawn at once; bounds bitmaps in flight and keeps the queue short enough to drop scrolled-away pages.
    private const int MaxInFlight = 2;
    // Pixels left around a fitted page, and between pages.
    private const double FitMargin = 32;
    private const double PageGap = 12;
    private const double ThumbnailWidth = 120;
    // Highlight colours when the theme has none. Shared, so an unchanged highlight compares equal and is not redrawn.
    private static readonly IBrush FoundFallback = new ImmutableSolidColorBrush(Colors.Gold, 0.4);
    private static readonly IBrush SelectedFallback = new ImmutableSolidColorBrush(Colors.Orange, 0.7);

    private readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel pageStack = new() { Spacing = PageGap, Margin = new Thickness(FitMargin / 2), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock textError = new() { Text = "PDF page text could not be read.", IsVisible = false, Margin = new Thickness(8, 4) };
    private readonly ComboBox zoomList = new() { MinWidth = 110 };
    private readonly Button zoomOut;
    private readonly Button zoomIn;
    private readonly ToggleButton sideToggle = new() { Content = "Panel" };
    private readonly Border side = new() { Width = 200, IsVisible = false };
    private readonly ScrollViewer thumbScroller = new();
    private readonly StackPanel thumbStack = new() { Spacing = 8, Margin = new Thickness(8) };
    private readonly TreeView bookmarks = new();
    private readonly TextBlock noBookmarks = new() { Text = "This document has no bookmarks.", Margin = new Thickness(8) };
    private readonly Queue<TileRequest> queue = new();
    private readonly List<FindMatch> matches = [];
    private PageState[] pages = [];
    private Image?[] thumbnails = [];
    private IReadOnlyList<PdfTextLine>?[] texts = [];
    private CancellationTokenSource work = new();
    private double scale = PixelsPerPoint;
    private int generation;
    private int inFlight;
    private int currentMatch = -1;
    // Counts Find calls, so a page knows whether the matches it highlighted are still the current ones.
    private int findRevision;
    private bool layoutQueued;
    private bool syncingZoom;
    private bool disposed;

    /// <summary>Creates the viewer.</summary>
    public PdfViewer()
    {
        this.zoomOut = ToolButton("−", "Zoom out", () => this.Step(-1));
        this.zoomIn = ToolButton("+", "Zoom in", () => this.Step(1));
        foreach (int percent in PdfZoom.Percents) this.zoomList.Items.Add(new ComboBoxItem { Content = $"{percent}%", Tag = new PdfZoom.Percent(percent) });
        this.zoomList.Items.Add(new ComboBoxItem { Content = "Fit width", Tag = new PdfZoom.FitWidth() });
        this.zoomList.Items.Add(new ComboBoxItem { Content = "Fit page", Tag = new PdfZoom.FitPage() });
        global::Avalonia.Automation.AutomationProperties.SetName(this.zoomList, "Zoom");
        this.zoomList.SelectionChanged += (_, _) =>
        {
            if (!this.syncingZoom && this.zoomList.SelectedItem is ComboBoxItem { Tag: PdfZoom zoom }) this.Zoom = zoom;
        };
        global::Avalonia.Automation.AutomationProperties.SetName(this.sideToggle, "Show or hide the side panel");
        ToolTip.SetTip(this.sideToggle, "Side panel");
        this.sideToggle.IsCheckedChanged += (_, _) =>
        {
            this.side.IsVisible = this.sideToggle.IsChecked == true;
            this.QueueLayout();
        };

        StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4) };
        toolbar.Classes.Add("pdf-toolbar");
        toolbar.Children.Add(this.sideToggle);
        toolbar.Children.Add(this.zoomOut);
        toolbar.Children.Add(this.zoomList);
        toolbar.Children.Add(this.zoomIn);
        toolbar.Children.Add(this.status);

        this.thumbScroller.Content = this.thumbStack;
        this.thumbScroller.ScrollChanged += (_, _) => this.LoadThumbnails();
        this.bookmarks.SelectionChanged += (_, _) =>
        {
            if (this.bookmarks.SelectedItem is TreeViewItem { Tag: PdfBookmark bookmark }) _ = this.GoToBookmarkAsync(bookmark);
        };
        TabControl tabs = new()
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Thumbnails", Content = this.thumbScroller },
                new TabItem { Header = "Bookmarks", Content = new Panel { Children = { this.bookmarks, this.noBookmarks } } },
            },
        };
        tabs.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(this.LoadThumbnails, DispatcherPriority.Background);
        this.side.Child = tabs;
        this.side.Classes.Add("pdf-side");

        this.scroller.Content = this.pageStack;
        this.scroller.ScrollChanged += (_, _) => this.QueueLayout();
        this.scroller.SizeChanged += (_, args) =>
        {
            // A fitted zoom refits on a resize; a resize that leaves the fitted scale as it was keeps every drawn tile.
            if (this.Zoom is not PdfZoom.Percent && args.PreviousSize != args.NewSize && this.pages.Length > 0
                && this.TargetScale(this.CurrentPage) != this.scale) this.ApplyZoom(keepPage: true);
        };
        global::Avalonia.Automation.AutomationProperties.SetName(this.scroller, "PDF pages");

        DockPanel body = new();
        DockPanel.SetDock(this.side, Dock.Left);
        body.Children.Add(this.side);
        body.Children.Add(this.scroller);
        DockPanel layout = new();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(this.textError, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(this.textError);
        layout.Children.Add(body);
        this.Template = new FuncControlTemplate<PdfViewer>((_, _) => layout);
        this.SyncZoomList();
    }

    /// <summary>The document shown, or null for none. The caller opens it and disposes it after the viewer lets it go.</summary>
    public IPdfDocument? Document
    {
        get => this.GetValue(DocumentProperty);
        set => this.SetValue(DocumentProperty, value);
    }

    /// <summary>The zoom the pages are drawn at. Every document opens at <see cref="PdfZoom.FitWidth"/> unless the caller sets one.</summary>
    public PdfZoom Zoom
    {
        get => this.GetValue(ZoomProperty);
        set => this.SetValue(ZoomProperty, value);
    }

    /// <summary>The 0-based page at the top of the view.</summary>
    public int CurrentPage { get; private set; }

    /// <summary>The percentage the pages are drawn at, whatever the zoom: a fitted zoom reports the size it fitted to.</summary>
    public int EffectivePercent => (int)Math.Round(this.scale / PixelsPerPoint * 100);

    /// <summary>Scrolls page <paramref name="page"/> (0-based) to the top, or the point <paramref name="top"/> points below its top.</summary>
    public void GoToPage(int page, float? top = null)
    {
        if (this.pages.Length == 0) return;
        int index = Math.Clamp(page, 0, this.pages.Length - 1);
        double y = this.PageTop(index) + (top is { } offset && offset >= 0 ? offset * this.scale : 0);
        this.scroller.Offset = new Vector(this.scroller.Offset.X, y);
        this.CurrentPage = index;
        this.UpdateStatus();
        this.QueueLayout();
    }

    /// <summary>Highlights every case-insensitive match of <paramref name="query"/> in the text read so far, shows the first, and returns the count.</summary>
    public int Find(string query)
    {
        this.matches.Clear();
        this.currentMatch = -1;
        this.findRevision++;
        if (!string.IsNullOrEmpty(query))
        {
            for (int page = 0; page < this.texts.Length; page++)
            {
                if (this.texts[page] is not { } lines) continue;
                for (int line = 0; line < lines.Count; line++)
                {
                    string text = lines[line].Text;
                    for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                        at = text.IndexOf(query, at + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase))
                        this.matches.Add(new(page, line, at, query.Length));
                }
            }
        }
        if (this.matches.Count > 0) this.ShowMatch(0);
        else this.DrawHighlights();
        return this.matches.Count;
    }

    /// <summary>Shows the match after the current one, wrapping to the first.</summary>
    public void FindNext()
    {
        if (this.matches.Count > 0) this.ShowMatch((this.currentMatch + 1) % this.matches.Count);
    }

    /// <summary>Shows the match before the current one, wrapping to the last.</summary>
    public void FindPrevious()
    {
        if (this.matches.Count > 0) this.ShowMatch((this.currentMatch - 1 + this.matches.Count) % this.matches.Count);
    }

    /// <summary>The one-based index of the shown match, or 0 when none is shown.</summary>
    public int CurrentMatch => this.currentMatch + 1;

    /// <summary>Stops drawing and reading, and releases every drawn bitmap. The document stays open for its owner.</summary>
    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.work.Cancel();
        this.Clear();
        this.work.Dispose();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty) this.Load();
        else if (change.Property == ZoomProperty)
        {
            this.SyncZoomList();
            this.ApplyZoom(keepPage: true);
        }
    }

    private static Button ToolButton(string text, string name, Action click)
    {
        Button button = new() { Content = text };
        button.Classes.Add("ghost");
        global::Avalonia.Automation.AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private string Label(int index) => this.Document?.Pages[index].Label ?? (index + 1).ToString(CultureInfo.InvariantCulture);

    private void Clear()
    {
        this.work.Cancel();
        if (!this.disposed)
        {
            this.work.Dispose();
            this.work = new CancellationTokenSource();
        }
        this.queue.Clear();
        this.generation++;
        foreach (PageState page in this.pages) page.DropTiles();
        foreach (Image? thumbnail in this.thumbnails) (thumbnail?.Source as IDisposable)?.Dispose();
        this.pages = [];
        this.thumbnails = [];
        this.texts = [];
        this.matches.Clear();
        this.currentMatch = -1;
        this.pageStack.Children.Clear();
        this.thumbStack.Children.Clear();
        this.bookmarks.ItemsSource = null;
        this.textError.IsVisible = false;
    }

    private void Load()
    {
        this.Clear();
        if (this.disposed || this.Document is not { } document) return;
        this.pages = [.. document.Pages.Select((page, index) => new PageState(this, index, page))];
        foreach (PageState page in this.pages) this.pageStack.Children.Add(page.View);
        this.texts = new IReadOnlyList<PdfTextLine>?[this.pages.Length];
        this.thumbnails = new Image?[this.pages.Length];
        for (int index = 0; index < this.pages.Length; index++)
        {
            int target = index;
            PdfPage page = document.Pages[index];
            Image image = new() { Width = ThumbnailWidth, Height = ThumbnailWidth * page.Height / Math.Max(1, page.Width), Stretch = Stretch.Uniform };
            this.thumbnails[index] = image;
            StackPanel content = new() { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Border { Child = image, Background = Brushes.White });
            content.Children.Add(new TextBlock { Text = this.Label(index), HorizontalAlignment = HorizontalAlignment.Center });
            Button thumb = new() { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
            thumb.Classes.Add("ghost");
            thumb.Classes.Add("pdf-thumbnail");
            global::Avalonia.Automation.AutomationProperties.SetName(thumb, $"Page {this.Label(index)}");
            thumb.Click += (_, _) => this.GoToPage(target);
            this.thumbStack.Children.Add(thumb);
        }
        List<TreeViewItem> outline = Outline(document.Bookmarks);
        this.bookmarks.ItemsSource = outline;
        this.bookmarks.IsVisible = outline.Count > 0;
        this.noBookmarks.IsVisible = outline.Count == 0;
        this.ApplyZoom(keepPage: false);
        _ = this.ReadTextAsync(document, this.work.Token);
    }

    private static List<TreeViewItem> Outline(IReadOnlyList<PdfBookmark> items) =>
        [.. items.Select(item => new TreeViewItem { Header = item.Title, Tag = item, ItemsSource = Outline(item.Children) })];

    private async Task GoToBookmarkAsync(PdfBookmark bookmark)
    {
        if (bookmark.Page is not int target || this.Document is not { } document) return;
        float? top;
        try { top = await document.BookmarkTopAsync(bookmark.Id, this.work.Token).ConfigureAwait(true); }
        catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { return; }
        this.GoToPage(target, top);
    }

    // Every page's text, in order, for selection and find. A failure is reported once and leaves that page without text.
    private async Task ReadTextAsync(IPdfDocument document, CancellationToken cancellationToken)
    {
        for (int index = 0; index < this.pages.Length; index++)
        {
            try { this.texts[index] = await document.TextAsync(index, cancellationToken).ConfigureAwait(true); }
            catch (IOException) { this.textError.IsVisible = true; }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            this.QueueLayout();
        }
    }

    private void SyncZoomList()
    {
        this.syncingZoom = true;
        this.zoomList.SelectedItem = this.zoomList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, this.Zoom));
        this.syncingZoom = false;
    }

    private void Step(int direction)
    {
        int effective = this.EffectivePercent;
        int next = direction < 0 ? PdfZoom.Percents.LastOrDefault(percent => percent < effective) : PdfZoom.Percents.FirstOrDefault(percent => percent > effective);
        if (next > 0) this.Zoom = new PdfZoom.Percent(next);
    }

    private void ApplyZoom(bool keepPage)
    {
        if (this.pages.Length == 0)
        {
            this.UpdateStatus();
            return;
        }
        int anchor = keepPage ? this.CurrentPage : 0;
        this.scale = this.TargetScale(anchor);
        this.generation++;
        this.queue.Clear();
        foreach (PageState page in this.pages) page.Resize(this.scale);
        this.zoomOut.IsEnabled = this.EffectivePercent > PdfZoom.Percents[0];
        this.zoomIn.IsEnabled = this.EffectivePercent < PdfZoom.Percents[^1];
        Dispatcher.UIThread.Post(() => this.GoToPage(anchor), DispatcherPriority.Loaded);
    }

    // The scale the zoom asks for at the scroller's current size. Fit page fits page anchor; fit width fits the widest page.
    private double TargetScale(int anchor)
    {
        Size view = this.scroller.Bounds.Size;
        double width = Math.Max(1, view.Width - FitMargin), height = Math.Max(1, view.Height - FitMargin);
        return this.Zoom switch
        {
            PdfZoom.Percent percent => Math.Clamp(percent.Value, PdfZoom.Percents[0], PdfZoom.Percents[^1]) / 100d * PixelsPerPoint,
            PdfZoom.FitPage when this.Document is { } document => Math.Min(width / document.Pages[anchor].Width, height / document.Pages[anchor].Height),
            _ when this.Document is { } document && view.Width > 0 => width / document.Pages.Max(page => page.Width),
            _ => PixelsPerPoint,
        };
    }

    private double PageTop(int index)
    {
        double top = FitMargin / 2;
        for (int i = 0; i < index; i++) top += this.pages[i].Height + PageGap;
        return top;
    }

    private void UpdateStatus()
    {
        int count = this.pages.Length;
        this.status.Text = count == 0 ? string.Empty
            : this.Document?.Pages[this.CurrentPage].Label is { } label ? $"Page {label} ({this.CurrentPage + 1} of {count})"
            : $"Page {this.CurrentPage + 1} of {count}";
    }

    private void QueueLayout()
    {
        if (this.layoutQueued || this.disposed) return;
        this.layoutQueued = true;
        Dispatcher.UIThread.Post(this.Layout, DispatcherPriority.Background);
    }

    // Finds the page at the top, drops tiles far from the view, queues tiles near it, and builds text and links on visible pages.
    private void Layout()
    {
        this.layoutQueued = false;
        if (this.disposed || this.pages.Length == 0) return;
        double viewTop = this.scroller.Offset.Y, viewHeight = Math.Max(1, this.scroller.Viewport.Height);
        double ratio = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double device = this.scale * ratio;
        int topPage = -1;
        double top = FitMargin / 2;
        foreach (PageState page in this.pages)
        {
            double pageTop = top - viewTop, pageBottom = pageTop + page.Height;
            top += page.Height + PageGap;
            if (topPage < 0 && pageBottom > 1) topPage = page.Index;
            if (pageBottom < -3 * viewHeight || pageTop > 4 * viewHeight)
            {
                page.DropTiles();
                continue;
            }
            if (pageBottom < -viewHeight || pageTop > 2 * viewHeight) continue;
            if (pageBottom > 0 && pageTop < viewHeight)
            {
                page.ShowText(this.texts[page.Index], this.scale);
                page.LoadLinks();
            }
            if (page.Failed) continue;
            int width = (int)Math.Round(page.Width * device / this.scale), height = (int)Math.Round(page.Height * device / this.scale);
            for (int y = 0; y < height; y += TileSize)
            {
                double tileTop = pageTop + (y / ratio), tileBottom = tileTop + (TileSize / ratio);
                if (tileBottom < -viewHeight || tileTop > 2 * viewHeight) continue;
                for (int x = 0; x < width; x += TileSize)
                {
                    if (!page.Claim(x, y)) continue;
                    this.queue.Enqueue(new(page, this.generation, device, ratio, new(x, y, Math.Min(TileSize, width - x), Math.Min(TileSize, height - y))));
                }
            }
        }
        if (topPage >= 0 && topPage != this.CurrentPage)
        {
            this.CurrentPage = topPage;
            this.UpdateStatus();
        }
        this.DrawHighlights();
        this.Pump();
    }

    private void Pump()
    {
        while (!this.disposed && this.inFlight < MaxInFlight && this.queue.TryDequeue(out TileRequest? request))
        {
            if (request.Generation != this.generation || !request.Page.Holds(request.Area.X, request.Area.Y)) continue;
            this.inFlight++;
            _ = this.DrawTileAsync(request);
        }
    }

    private async Task DrawTileAsync(TileRequest request)
    {
        try
        {
            if (this.Document is not { } document) return;
            PdfBitmap? bitmap;
            try { bitmap = await document.RenderAsync(request.Page.Index, request.Scale, request.Area, this.work.Token).ConfigureAwait(true); }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException or ArgumentOutOfRangeException)
            {
                request.Page.Release(request.Area.X, request.Area.Y);
                return;
            }
            if (this.disposed || request.Generation != this.generation || !request.Page.Holds(request.Area.X, request.Area.Y)) return;
            if (bitmap is null)
            {
                request.Page.ShowFailure();
                return;
            }
            request.Page.Place(request.Area, request.Ratio, ToBitmap(bitmap, request.Ratio));
        }
        finally
        {
            this.inFlight--;
            this.Pump();
        }
    }

    private static WriteableBitmap ToBitmap(PdfBitmap source, double ratio)
    {
        WriteableBitmap bitmap = new(new PixelSize(source.Width, source.Height), new Vector(96 * ratio, 96 * ratio), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer target = bitmap.Lock();
        int row = source.Width * 4;
        for (int y = 0; y < source.Height; y++) Marshal.Copy(source.Rgba, y * row, target.Address + (y * target.RowBytes), row);
        return bitmap;
    }

    private void LoadThumbnails()
    {
        if (this.disposed || !this.side.IsVisible || this.Document is not { } document) return;
        double viewTop = this.thumbScroller.Offset.Y, viewBottom = viewTop + this.thumbScroller.Viewport.Height;
        double ratio = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        for (int index = 0; index < this.thumbnails.Length; index++)
        {
            if (this.thumbnails[index] is not { Source: null } image || this.thumbStack.Children[index] is not Control holder) continue;
            if (holder.Bounds.Bottom < viewTop || holder.Bounds.Top > viewBottom + 200) continue;
            PdfPage page = document.Pages[index];
            double device = ThumbnailWidth * ratio / Math.Max(1, page.Width);
            PdfPixelArea area = new(0, 0, Math.Clamp((int)Math.Round(page.Width * device), 1, TileSize), Math.Clamp((int)Math.Round(page.Height * device), 1, TileSize));
            image.Source = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            _ = this.DrawThumbnailAsync(document, index, image, device, area, ratio);
        }
    }

    private async Task DrawThumbnailAsync(IPdfDocument document, int index, Image image, double device, PdfPixelArea area, double ratio)
    {
        PdfBitmap? bitmap;
        try { bitmap = await document.RenderAsync(index, device, area, this.work.Token).ConfigureAwait(true); }
        catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException or ArgumentOutOfRangeException) { return; }
        if (this.disposed || bitmap is null) return;
        (image.Source as IDisposable)?.Dispose();
        image.Source = ToBitmap(bitmap, ratio);
    }

    private void ShowMatch(int index)
    {
        this.currentMatch = index;
        FindMatch match = this.matches[index];
        PdfRect box = this.texts[match.Page]![match.Line].Box;
        this.GoToPage(match.Page, Math.Max(0, box.Top - 40));
        this.DrawHighlights();
    }

    // Draws the highlights of each page with a text layer whose highlights are not already those of the current find.
    private void DrawHighlights()
    {
        IBrush found = this.Brush("accent.brush.30", FoundFallback);
        IBrush selected = this.Brush("accent.brush.60", SelectedFallback);
        HighlightKey key = new(this.findRevision, this.currentMatch, found, selected);
        foreach (PageState page in this.pages)
        {
            if (!page.HasLayers || page.Highlighted == key) continue;
            List<(PdfRect Box, IBrush Brush)> boxes = [];
            for (int i = 0; i < this.matches.Count; i++)
            {
                FindMatch match = this.matches[i];
                if (match.Page != page.Index) continue;
                PdfTextLine line = this.texts[match.Page]![match.Line];
                double share = line.Box.Width / Math.Max(1, line.Text.Length);
                boxes.Add((new PdfRect(line.Box.Left + (float)(share * match.Start), line.Box.Top, (float)(share * match.Length), line.Box.Height),
                    i == this.currentMatch ? selected : found));
            }
            page.ShowHighlights(key, boxes, this.scale);
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, this.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;

    private sealed record TileRequest(PageState Page, int Generation, double Scale, double Ratio, PdfPixelArea Area);

    private sealed record FindMatch(int Page, int Line, int Start, int Length);

    // What a page's highlights show: the find they came from, the shown match and the two colours.
    private sealed record HighlightKey(int Find, int Current, IBrush Found, IBrush Selected);

    /// <summary>One page's box and its drawn tiles, text, links and highlights.</summary>
    private sealed class PageState
    {
        private readonly PdfViewer owner;
        private readonly PdfPage page;
        private readonly Canvas tiles = new();
        private readonly Canvas text = new();
        private readonly Canvas highlights = new() { IsHitTestVisible = false };
        private readonly Canvas links = new();
        private readonly Dictionary<(int X, int Y), Image?> drawn = [];
        private double textScale = -1;
        private bool linksLoaded;

        public PageState(PdfViewer owner, int index, PdfPage page)
        {
            this.owner = owner;
            this.Index = index;
            this.page = page;
            this.View = new Panel { Background = Brushes.White, ClipToBounds = true, Children = { this.tiles, this.highlights, this.text, this.links } };
            this.View.Classes.Add("pdf-page");
        }

        public int Index { get; }

        public Panel View { get; }

        public bool Failed { get; private set; }

        public double Width => this.View.Width;

        public double Height => this.View.Height;

        public bool HasLayers => this.textScale > 0;

        /// <summary>What the page's highlights show now, or null when it shows none drawn for its current text layer.</summary>
        public HighlightKey? Highlighted { get; private set; }

        public void Resize(double scale)
        {
            this.View.Width = this.page.Width * scale;
            this.View.Height = this.page.Height * scale;
            this.DropTiles();
            this.text.Children.Clear();
            this.highlights.Children.Clear();
            this.Highlighted = null;
            this.links.Children.Clear();
            this.textScale = -1;
            this.linksLoaded = false;
        }

        public bool Claim(int x, int y) => this.drawn.TryAdd((x, y), null);

        public bool Holds(int x, int y) => this.drawn.ContainsKey((x, y));

        public void Release(int x, int y) => this.drawn.Remove((x, y));

        public void Place(PdfPixelArea area, double ratio, WriteableBitmap bitmap)
        {
            Image image = new() { Source = bitmap, Width = area.Width / ratio, Height = area.Height / ratio, Stretch = Stretch.Fill };
            Canvas.SetLeft(image, area.X / ratio);
            Canvas.SetTop(image, area.Y / ratio);
            this.drawn[(area.X, area.Y)] = image;
            this.tiles.Children.Add(image);
        }

        public void DropTiles()
        {
            foreach (Image? image in this.drawn.Values) (image?.Source as IDisposable)?.Dispose();
            this.drawn.Clear();
            this.tiles.Children.Clear();
        }

        public void ShowFailure()
        {
            if (this.Failed) return;
            this.Failed = true;
            this.View.Children.Add(new TextBlock
            {
                Text = "This page could not be displayed.",
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Each line is a selectable, transparent text block over the drawn page, stretched to the width of its box.
        public void ShowText(IReadOnlyList<PdfTextLine>? lines, double scale)
        {
            if (lines is null || this.textScale == scale) return;
            this.textScale = scale;
            this.text.Children.Clear();
            IBrush selection = this.owner.Brush("accent.brush.40", new SolidColorBrush(Colors.DodgerBlue, 0.4));
            foreach (PdfTextLine line in lines)
            {
                SelectableTextBlock block = new()
                {
                    Text = line.Text,
                    Foreground = Brushes.Transparent,
                    SelectionBrush = selection,
                    FontSize = Math.Max(1, line.Box.Height * scale * 0.85),
                    RenderTransformOrigin = RelativePoint.TopLeft,
                };
                block.Measure(Size.Infinity);
                double natural = block.DesiredSize.Width, wanted = line.Box.Width * scale;
                if (natural > 0 && wanted > 0) block.RenderTransform = new ScaleTransform(wanted / natural, 1);
                Canvas.SetLeft(block, line.Box.Left * scale);
                Canvas.SetTop(block, line.Box.Top * scale);
                this.text.Children.Add(block);
            }
        }

        public void ShowHighlights(HighlightKey key, List<(PdfRect Box, IBrush Brush)> boxes, double scale)
        {
            this.Highlighted = key;
            this.highlights.Children.Clear();
            foreach ((PdfRect box, IBrush brush) in boxes)
            {
                Border mark = new() { Background = brush, Width = box.Width * scale, Height = box.Height * scale, CornerRadius = new CornerRadius(2) };
                Canvas.SetLeft(mark, box.Left * scale);
                Canvas.SetTop(mark, box.Top * scale);
                this.highlights.Children.Add(mark);
            }
        }

        public void LoadLinks()
        {
            if (this.linksLoaded || this.owner.Document is not { } document) return;
            this.linksLoaded = true;
            _ = this.LoadLinksAsync(document, this.owner.scale);
        }

        private async Task LoadLinksAsync(IPdfDocument document, double scale)
        {
            IReadOnlyList<PdfLink> found;
            try { found = await document.LinksAsync(this.Index, this.owner.work.Token).ConfigureAwait(true); }
            catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { return; }
            if (this.owner.disposed || this.owner.scale != scale) return;
            foreach (PdfLink link in found)
            {
                Border area = new()
                {
                    Background = Brushes.Transparent,
                    Width = link.Box.Width * scale,
                    Height = link.Box.Height * scale,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                area.Classes.Add("pdf-link");
                Canvas.SetLeft(area, link.Box.Left * scale);
                Canvas.SetTop(area, link.Box.Top * scale);
                area.PointerReleased += (_, _) => this.owner.GoToPage(link.TargetPage, link.TargetTop);
                this.links.Children.Add(area);
            }
        }
    }
}
