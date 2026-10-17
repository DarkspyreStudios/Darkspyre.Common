using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Darkspyre.Common.Avalonia.Markdown;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class MarkdownViewerTests
{
    [Fact]
    public Task RawHtmlShowsAsTextAndNeverBecomesAnImageOrHandler() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Text = "Before\n\n<script>alert('x')</script>\n\n<img src=\"x\" onerror=\"alert(1)\">\n\nAfter" };
        ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("After", StringComparison.Ordinal));

        string text = Text(viewer);
        Assert.Contains("<script>alert('x')</script>", text, StringComparison.Ordinal);
        Assert.Contains("onerror", text, StringComparison.Ordinal);
        Assert.Empty(ViewSession.All<Image>(viewer));
    });

    [Fact]
    public void ADisallowedLinkTargetIsEmptiedAndAnAllowedOneKept()
    {
        MarkdownDocument document = Markdig.Markdown.Parse("[bad](javascript:alert(1)) [odd](//host/x) [good](https://example.com/a) [local](docs/readme.md)",
            MarkdownViewer.Pipeline);

        string[] targets = [.. document.Descendants<LinkInline>().Select(link => link.Url ?? "(null)")];

        Assert.Equal([string.Empty, string.Empty, "https://example.com/a", "docs/readme.md"], targets);
    }

    [Fact]
    public Task OnlyAnAllowedLinkIsActivated() => ViewSession.RunAsync(() =>
    {
        MarkdownViewer viewer = new();
        ViewSession.Show(viewer);
        List<string> activated = [];
        viewer.LinkActivated += (_, args) => activated.Add(args.Target);
        MarkdownRenderer renderer = Assert.Single(ViewSession.All<MarkdownRenderer>(viewer));

        renderer.LinkCommand!.Execute(new Uri("javascript:alert(1)"));
        renderer.LinkCommand.Execute(new Uri("https://example.com/a"));
        renderer.LinkCommand.Execute(new Uri("mailto:someone@example.com"));

        Assert.Equal(["https://example.com/a", "mailto:someone@example.com"], activated);
        return Task.CompletedTask;
    });

    [Fact]
    public Task MermaidSourceThatCannotBeDrawnStaysACodeBlock() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Text = "```mermaid\nthis is not a diagram at all\n```" };
        ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => ViewSession.All<MermaidView>(viewer).Any());
        MermaidView diagram = Assert.Single(ViewSession.All<MermaidView>(viewer));

        // Give the renderer time to answer; the fence must still be code afterwards.
        await Task.Delay(500);
        ViewSession.Settle();

        Assert.IsType<LiveMarkdown.Avalonia.CodeBlock>(diagram.Content);
    });

    [Fact]
    public Task AMermaidDiagramIsDrawnAsAPicture() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Text = "Intro\n\n```mermaid\ngraph TD\n  A[Start] --> B[End]\n```" };
        ViewSession.Show(viewer);

        await ViewSession.WaitForAsync(() => ViewSession.All<MermaidView>(viewer).Any(view => view.Content is Image));
    });

    [Fact]
    public Task StreamedTextGrowsAndAReplacementReplacesIt() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Streaming = true };
        ViewSession.Show(viewer);
        viewer.Text = "First part";
        viewer.Text = "First part and more";
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("First part and more", StringComparison.Ordinal));

        viewer.Streaming = false;
        viewer.Text = "Something else";
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("Something else", StringComparison.Ordinal));
        Assert.DoesNotContain("First part", Text(viewer), StringComparison.Ordinal);
    });

    /// <summary>
    /// LiveMarkdown's own styles set Arial at 14 (headings 24 to 16) on every text block, whatever the parent's font.
    /// Paragraphs, quotes, list items, table cells and list markers take the body entry, each heading level its own
    /// entry, and code the code entry; a change to one entry restyles only that entry's text.
    /// </summary>
    [Fact]
    public Task MarkdownTextAndHeadingsTakeTheEntryFonts() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new()
        {
            Text = "# One\n\n## Two\n\n### Three\n\n#### Four\n\n##### Five\n\n###### Six\n\nPara `inline`\n\n> Quote\n\n- Item\n\n1. Numbered\n\n"
                + "| Head |\n| --- |\n| Cell |\n\n```\nblock\n```\n\nEnd",
        };
        Border host = new() { Child = viewer };
        FontFamily body = new("Courier New"), code = new("Menlo");
        host.Resources["font-body"] = body;
        host.Resources["font-size-body"] = 21d;
        host.Resources["font-code"] = code;
        host.Resources["font-size-code"] = 17d;
        for (int level = 1; level <= 6; level++)
        {
            host.Resources[$"font-heading-{level}"] = new FontFamily($"Heading Face {level}");
            host.Resources[$"font-size-heading-{level}"] = 40d - level;
        }
        ViewSession.Show(host);
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("End", StringComparison.Ordinal));
        MarkdownTextBlock[] blocks = [.. ViewSession.All<MarkdownTextBlock>(viewer)];

        MarkdownTextBlock[] paragraphs = [.. blocks.Where(block => block.Classes.Contains("ParagraphBlock"))];
        Assert.Equal(["Para inline", "Quote", "Item", "Numbered", "Head", "Cell", "End"], paragraphs.Select(block => block.ActualText.Trim()));
        Assert.All(paragraphs, block => Assert.Equal((body, 21d), (block.FontFamily, block.FontSize)));
        TextBlock[] markers = [.. ViewSession.All<TextBlock>(viewer).Where(block => block.Classes.Contains("ListBlockBullet") || block.Classes.Contains("ListBlockNumber"))];
        Assert.Equal(2, markers.Length);
        Assert.All(markers, marker => Assert.Equal((body, 21d), (marker.FontFamily, marker.FontSize)));
        for (int level = 1; level <= 6; level++)
        {
            MarkdownTextBlock heading = Assert.Single(blocks, block => block.Classes.Contains($"Heading{level}"));
            Assert.Equal((new FontFamily($"Heading Face {level}"), 40d - level), (heading.FontFamily, heading.FontSize));
        }
        MarkdownRenderer renderer = Assert.Single(ViewSession.All<MarkdownRenderer>(viewer));
        LiveMarkdown.Avalonia.CodeInline inline = Assert.Single(renderer.GetLogicalDescendants().OfType<LiveMarkdown.Avalonia.CodeInline>());
        Assert.Equal((code, 17d), (inline.FontFamily, inline.FontSize));
        LiveMarkdown.Avalonia.CodeBlock fence = Assert.Single(ViewSession.All<LiveMarkdown.Avalonia.CodeBlock>(viewer));
        Assert.Equal((code, 17d), (fence.FontFamily, fence.FontSize));

        host.Resources["font-size-heading-1"] = 50d;
        ViewSession.Settle();

        Assert.Equal(50d, Assert.Single(blocks, block => block.Classes.Contains("Heading1")).FontSize);
        Assert.Equal(38d, Assert.Single(blocks, block => block.Classes.Contains("Heading2")).FontSize);
        Assert.All(paragraphs, block => Assert.Equal(21d, block.FontSize));
    });

    private static string Text(MarkdownViewer viewer) => string.Join("\n", ViewSession.All<MarkdownTextBlock>(viewer).Select(block => block.ActualText));
}
