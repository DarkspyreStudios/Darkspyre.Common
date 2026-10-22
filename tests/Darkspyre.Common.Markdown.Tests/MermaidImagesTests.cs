using System.Text;
using Darkspyre.Common.Markdown.Mermaid;
using Markdig;
using Markdig.Syntax;
using Xunit;

namespace Darkspyre.Common.Markdown.Tests;

public sealed class MermaidImagesTests
{
    // The light values of mobile.css: --panel, --text, --line, --accent.
    private static readonly MermaidTheme PhoneTheme = new("#f4f4f5", "#27272a", "#e4e4e7", "#3b82f6", "sans-serif");

    [Fact]
    public async Task MermaidFenceRendersAsAnImageOnThePhone()
    {
        MarkdownPipelineBuilder builder = MarkdownSafety.CreateBuilder(null);
        builder.Extensions.Add(new MermaidImageExtension());
        MarkdownPipeline pipeline = MarkdownSafety.Build(builder);
        MermaidImages images = new(new MermaiderRenderer());

        async Task<string> HtmlOf(string text)
        {
            MarkdownDocument document = Markdig.Markdown.Parse(text, pipeline);
            await images.DrawAsync(MermaidImages.FencesIn(document), PhoneTheme);
            images.Apply(document, PhoneTheme);
            return document.ToHtml(pipeline);
        }

        string diagram = await HtmlOf("Intro\n\n```mermaid\ngraph TD\n  A[Start] --> B[End]\n```\n");
        const string Prefix = "<img alt=\"diagram\" src=\"data:image/svg+xml;base64,";
        int start = diagram.IndexOf(Prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, diagram);
        string encoded = diagram[(start + Prefix.Length)..diagram.IndexOf('"', start + Prefix.Length)];
        string svg = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.StartsWith("<svg", svg.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", diagram, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", diagram, StringComparison.OrdinalIgnoreCase);

        // A fence that cannot be drawn, and a fence a streamed reply has not closed yet, stay code.
        string failed = await HtmlOf("```mermaid\nthis is not a diagram at all\n```\n");
        Assert.Contains("<pre class=\"mermaid\">this is not a diagram at all", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", failed, StringComparison.Ordinal);
        string open = await HtmlOf("```mermaid\ngraph TD\n  A --> B\n");
        Assert.Contains("<pre class=\"mermaid\">graph TD", open, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", open, StringComparison.Ordinal);
    }
}
