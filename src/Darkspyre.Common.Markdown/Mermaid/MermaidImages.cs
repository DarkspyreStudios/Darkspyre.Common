using System.Collections.Concurrent;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Darkspyre.Common.Markdown.Mermaid;

/// <summary>
/// Draws the <c>mermaid</c> fences of a Markdown document as images for a surface that shows HTML. Each closed fence is drawn to
/// SVG off the caller's thread and kept as a <c>data:</c> address, by fence source and theme, so a streamed reply draws only its
/// new fences. <see cref="Apply"/> then replaces each drawn fence with an <c>img</c> element; a fence that could not be drawn, and
/// a fence still open, stay a code block.
/// </summary>
/// <remarks>
/// The image is an SVG in an <c>img</c> element, which runs no script and loads nothing. The page receives the SVG only as base64
/// in the element's <c>src</c>, never as markup. The pipeline that renders the document must include <see cref="MermaidImageExtension"/>.
/// </remarks>
/// <gotcha>The cache has no limit and no eviction: it holds one image per distinct closed fence and theme for the object's life.</gotcha>
public sealed class MermaidImages(IMermaidRenderer renderer)
{
    private const string DataUriPrefix = "data:image/svg+xml;base64,";

    private readonly ConcurrentDictionary<(string Source, MermaidTheme Theme), Task<string?>> images = new();

    /// <summary>The distinct sources of the document's closed <c>mermaid</c> fences, in order. An open fence, which a streamed reply
    /// has not finished, is left out so a half-written diagram is never drawn.</summary>
    public static IReadOnlyList<string> FencesIn(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Descendants<FencedCodeBlock>().Where(IsClosedMermaid).Select(fence => fence.Lines.ToString()).Distinct()];
    }

    /// <summary>Whether every source has finished drawing in <paramref name="theme"/>, drawn or not.</summary>
    public bool IsSettled(IEnumerable<string> sources, MermaidTheme theme)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(theme);
        return sources.All(source => this.images.TryGetValue((source, theme), out Task<string?>? drawn) && drawn.IsCompleted);
    }

    /// <summary>Draws each source not yet drawn in <paramref name="theme"/> on the thread pool and completes when all have finished.
    /// A source the renderer cannot draw, or a renderer that throws, leaves that fence as code; this method does not throw for it.</summary>
    public async Task DrawAsync(IEnumerable<string> sources, MermaidTheme theme)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(theme);
        Task<string?>[] runs = [.. sources.Distinct().Select(source => this.images.GetOrAdd((source, theme), key => Task.Run(() => this.DrawOneAsync(key.Source, key.Theme))))];
        await Task.WhenAll(runs).ConfigureAwait(false);
    }

    /// <summary>Replaces each closed <c>mermaid</c> fence drawn in <paramref name="theme"/> with an image. Other fences are kept.</summary>
    public void Apply(MarkdownDocument document, MermaidTheme theme)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(theme);
        foreach (FencedCodeBlock fence in document.Descendants<FencedCodeBlock>().Where(IsClosedMermaid).ToArray())
        {
            if (fence.Parent is not { } parent
                || !this.images.TryGetValue((fence.Lines.ToString(), theme), out Task<string?>? drawn)
                || !drawn.IsCompletedSuccessfully || drawn.Result is not { } uri)
                continue;
            int index = parent.IndexOf(fence);
            parent.RemoveAt(index);
            parent.Insert(index, new MermaidImageBlock(uri) { Span = fence.Span, Line = fence.Line, Column = fence.Column });
        }
    }

    private static bool IsClosedMermaid(FencedCodeBlock fence)
        => string.Equals(fence.Info, "mermaid", StringComparison.OrdinalIgnoreCase) && fence.ClosingFencedCharCount > 0;

    private async Task<string?> DrawOneAsync(string source, MermaidTheme theme)
    {
        try
        {
            string? svg = await renderer.RenderSvgAsync(source, theme, CancellationToken.None).ConfigureAwait(false);
            return svg is null ? null : DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        }
        // A renderer failure leaves the fence as code; it must not end the page's render.
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
}

/// <summary>A <c>mermaid</c> fence drawn as an image.</summary>
internal sealed class MermaidImageBlock : LeafBlock
{
    public MermaidImageBlock(string dataUri) : base(null) => this.DataUri = dataUri;

    /// <summary>A <c>data:image/svg+xml;base64,</c> address built by <see cref="MermaidImages"/>.</summary>
    public string DataUri { get; }
}

/// <summary>Writes a drawn <c>mermaid</c> fence as an <c>img</c> element with the alt text <c>diagram</c>.</summary>
internal sealed class MermaidImageBlockRenderer : HtmlObjectRenderer<MermaidImageBlock>
{
    protected override void Write(HtmlRenderer renderer, MermaidImageBlock block)
    {
        renderer.EnsureLine();
        renderer.Write("<img alt=\"diagram\" src=\"").WriteEscape(block.DataUri).Write("\">");
        renderer.WriteLine();
    }
}

/// <summary>Teaches the HTML renderer of a Markdown pipeline to write the images <see cref="MermaidImages.Apply"/> puts in a document.</summary>
public sealed class MermaidImageExtension : IMarkdownExtension
{
    /// <summary>Adds nothing to the parser: the images come from <see cref="MermaidImages.Apply"/>, not from parsing.</summary>
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    /// <summary>Adds the writer of drawn <c>mermaid</c> fences to an HTML renderer; other renderers are left unchanged.</summary>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html) html.ObjectRenderers.AddIfNotAlready<MermaidImageBlockRenderer>();
    }
}
