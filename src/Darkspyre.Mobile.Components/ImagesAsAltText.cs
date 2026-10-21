using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Darkspyre.Mobile.Components;

/// <summary>Replaces every image with its alt text, so the page loads no remote image.</summary>
internal sealed class ImagesAsAltText : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Replace;

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private static void Replace(MarkdownDocument document)
    {
        foreach (LinkInline image in document.Descendants<LinkInline>().Where(link => link.IsImage).ToList())
            image.ReplaceBy(new LiteralInline(string.Concat(image.Descendants<LiteralInline>().Select(text => text.Content.ToString()))) { Span = image.Span });
    }
}
