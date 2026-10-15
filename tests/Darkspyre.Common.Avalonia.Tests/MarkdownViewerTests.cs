using Avalonia.Controls;
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

    private static string Text(MarkdownViewer viewer) => string.Join("\n", ViewSession.All<MarkdownTextBlock>(viewer).Select(block => block.ActualText));
}
