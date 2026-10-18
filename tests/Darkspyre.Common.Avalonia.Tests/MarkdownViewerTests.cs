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
    /// A new viewer, and a replaced text, take their height in the layout that first places them, before any queued job
    /// runs. A height that arrived a frame later made the transcript jump and come back while a turn ran.
    /// </summary>
    [Fact]
    public Task ANewViewerTakesItsHeightInTheLayoutThatFirstPlacesIt() => ViewSession.RunAsync(() =>
    {
        StackPanel host = new();
        Window window = ViewSession.Show(host);
        const string First = "A first paragraph.\n\nA second paragraph.\n\nA third paragraph.";
        MarkdownViewer viewer = new() { Text = First };
        host.Children.Add(viewer);
        window.UpdateLayout();
        double placed = viewer.Bounds.Height;
        ViewSession.Settle();
        Assert.True(placed > 0, "The new viewer was laid out with no height.");
        Assert.Equal(viewer.Bounds.Height, placed, 1);

        viewer.Text = First + "\n\nA fourth paragraph.\n\nA fifth paragraph.";
        window.UpdateLayout();
        double replaced = viewer.Bounds.Height;
        ViewSession.Settle();
        Assert.True(replaced > placed, "The replaced text kept its old height.");
        Assert.Equal(viewer.Bounds.Height, replaced, 1);
        return Task.CompletedTask;
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

    /// <summary>
    /// The renderer's palette is written as one dictionary, and only when a colour differs: an equal colour or an unrelated
    /// resource leaves the dictionary the renderer shows as it was.
    /// </summary>
    [Fact]
    public Task ThePaletteIsSwappedWholeAndOnlyWhenAColourChanges() => ViewSession.RunAsync(() =>
    {
        MarkdownViewer viewer = new() { Text = "Text" };
        Border host = new() { Child = viewer };
        host.Resources["text"] = Colors.Red;
        host.Resources["panel"] = Colors.White;
        ViewSession.Show(host);
        MarkdownRenderer renderer = Assert.Single(ViewSession.All<MarkdownRenderer>(viewer));
        IResourceDictionary before = renderer.Resources;
        Assert.Equal(Colors.Red, before["ForegroundColor"]);

        host.Resources["text"] = Color.FromRgb(255, 0, 0);
        host.Resources["unrelated"] = 1d;
        ViewSession.Settle();
        Assert.Same(before, renderer.Resources);

        host.Resources["text"] = Colors.Blue;
        ViewSession.Settle();
        Assert.NotSame(before, renderer.Resources);
        Assert.Equal(Colors.Blue, renderer.Resources["ForegroundColor"]);
        Assert.Equal(Colors.White, renderer.Resources["CardBackgroundColor"]);
        return Task.CompletedTask;
    });

    /// <summary>A diagram is drawn again for a change to its colours, not for a resource that does not touch them.</summary>
    [Fact]
    public Task AMermaidDiagramIsNotDrawnAgainForAnUnrelatedResourceChange() => ViewSession.RunAsync(async () =>
    {
        CountingRenderer counting = new();
        MarkdownViewer viewer = new() { MermaidRenderer = counting, Text = "```mermaid\ngraph TD\n  A --> B\n```" };
        Border host = new() { Child = viewer };
        host.Resources["text"] = Colors.Red;
        ViewSession.Show(host);
        await ViewSession.WaitForAsync(() => counting.Calls > 0);
        await Task.Delay(100);
        ViewSession.Settle();
        int drawn = counting.Calls;

        host.Resources["unrelated"] = 1d;
        ViewSession.Settle();
        await Task.Delay(100);
        ViewSession.Settle();
        Assert.Equal(drawn, counting.Calls);

        host.Resources["text"] = Colors.Blue;
        await ViewSession.WaitForAsync(() => counting.Calls == drawn + 1);
    });

    /// <summary>Counts the diagrams asked for and draws none, so each fence stays code.</summary>
    private sealed class CountingRenderer : IMermaidRenderer
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public ValueTask<string?> RenderSvgAsync(string source, MermaidTheme theme, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.calls);
            return ValueTask.FromResult<string?>(null);
        }
    }

    private static string Text(MarkdownViewer viewer) => string.Join("\n", ViewSession.All<MarkdownTextBlock>(viewer).Select(block => block.ActualText));
}
