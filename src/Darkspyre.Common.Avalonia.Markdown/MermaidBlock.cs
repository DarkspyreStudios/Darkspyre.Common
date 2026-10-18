using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using SkiaSharp;
using Svg.Skia;
using Avalonia.VisualTree;
using CodeBlock = LiveMarkdown.Avalonia.CodeBlock;

namespace Darkspyre.Common.Avalonia.Markdown;

/// <summary>A <c>mermaid</c> fence, which the viewer draws as a diagram.</summary>
internal sealed class MermaidBlock : LeafBlock
{
    public MermaidBlock(FencedCodeBlock fence) : base(null)
    {
        this.Source = fence.Lines.ToString();
        this.Span = fence.Span;
        this.Line = fence.Line;
        this.Column = fence.Column;
    }

    /// <summary>The diagram source between the fences.</summary>
    public string Source { get; }
}

/// <summary>Replaces each <c>mermaid</c> fence in a parsed document with a <see cref="MermaidBlock"/>.</summary>
internal sealed class MermaidFences : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Replace;

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void Replace(MarkdownDocument document)
    {
        foreach (FencedCodeBlock fence in document.Descendants<FencedCodeBlock>().ToArray())
        {
            if (!string.Equals(fence.Info, "mermaid", StringComparison.OrdinalIgnoreCase) || fence.Parent is not { } parent) continue;
            int index = parent.IndexOf(fence);
            parent.RemoveAt(index);
            parent.Insert(index, new MermaidBlock(fence));
        }
    }
}

/// <summary>Shows a <see cref="MermaidBlock"/> through <see cref="MermaidView"/>.</summary>
internal sealed class MermaidNode : BlockNode<MermaidBlock>
{
    private readonly MermaidView view = new();

    public override Control Control => this.view;

    protected override bool UpdateCore(DocumentNode documentNode, MermaidBlock block, in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        this.view.Source = block.Source;
        return true;
    }
}

/// <summary>
/// A Mermaid diagram drawn by the nearest <see cref="MarkdownViewer"/>'s renderer in the current theme's colours. Until
/// the diagram is drawn, and when it cannot be, the source shows as a code block. A resource or theme change that leaves
/// the diagram's colours and font as they were draws nothing.
/// </summary>
internal sealed class MermaidView : ContentControl
{
    // Diagrams are rasterized at twice their size so they stay sharp on high-density screens.
    private const float Scale = 2f;

    private string source = string.Empty;
    private CancellationTokenSource? drawing;
    private MarkdownViewer? owner;
    // What the last draw that was started, and not cancelled, drew.
    private (string Source, MermaidTheme Theme, IMermaidRenderer Renderer)? drawn;

    public MermaidView()
    {
        this.Classes.Add("mermaid");
        this.ActualThemeVariantChanged += (_, _) => this.Draw();
    }

    public string Source
    {
        get => this.source;
        set
        {
            if (this.source == value) return;
            this.source = value;
            this.Draw();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.owner = this.FindAncestorOfType<MarkdownViewer>();
        if (this.owner is not null) this.owner.ThemeChanged += this.OnThemeChanged;
        this.Draw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A draw still running is cancelled, so it no longer counts as drawn.
        if (this.drawing is not null)
        {
            this.drawing.Cancel();
            this.drawing = null;
            this.drawn = null;
        }
        if (this.owner is not null) this.owner.ThemeChanged -= this.OnThemeChanged;
        this.owner = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs args) => this.Draw();

    private void Draw()
    {
        if (this.owner is not { } viewer || viewer.MermaidRenderer is not { } renderer || this.source.Length == 0)
        {
            this.drawing?.Cancel();
            this.drawing = null;
            this.drawn = null;
            this.ShowCode();
            return;
        }
        MermaidTheme theme = viewer.CurrentMermaidTheme();
        string text = this.source;
        if (this.drawn == (text, theme, renderer)) return;
        this.drawing?.Cancel();
        this.drawn = (text, theme, renderer);
        if (this.Content is not Image) this.ShowCode();
        CancellationTokenSource run = new();
        this.drawing = run;
        _ = Task.Run(async () =>
        {
            string? svg = await renderer.RenderSvgAsync(text, theme, run.Token).ConfigureAwait(false);
            return svg is null ? null : Rasterize(svg);
        }, run.Token).ContinueWith(done =>
        {
            if (done.IsCanceled) return;
            (byte[] Png, double Width, double Height)? picture = done.IsFaulted ? null : done.Result;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(this.drawing, run)) return;
                this.drawing = null;
                if (picture is not { } shown)
                {
                    this.ShowCode();
                    return;
                }
                using MemoryStream png = new(shown.Png);
                this.Content = new Image
                {
                    Source = new Bitmap(png),
                    Width = shown.Width,
                    Height = shown.Height,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
            });
        }, TaskScheduler.Default);
    }

    private void ShowCode()
    {
        if (this.Content is CodeBlock code && code.Code == this.source) return;
        this.Content = new CodeBlock { Language = "mermaid", Code = this.source };
    }

    // Draws the SVG with Svg.Skia and returns it as a PNG and its size in device-independent pixels, or null when it has no picture.
    private static (byte[] Png, double Width, double Height)? Rasterize(string svg)
    {
        using SKSvg document = new();
        SKPicture? picture = document.FromSvg(svg);
        if (picture is null) return null;
        SKRect bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        int width = (int)Math.Ceiling(bounds.Width * Scale), height = (int)Math.Ceiling(bounds.Height * Scale);
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), bounds.Width, bounds.Height);
    }
}
