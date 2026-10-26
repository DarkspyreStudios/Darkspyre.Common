using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Markdown;
using Darkspyre.Common.Markdown;
using Darkspyre.Common.Markdown.Mermaid;
using LiveMarkdown.Avalonia;
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

    /// <summary>
    /// The code-block copy button copies the code through the copy request, then shows a check named Copied for two
    /// seconds; a refused copy shows no check.
    /// </summary>
    [Fact]
    public Task CodeBlockCopyWritesTheCodeAndShowsACheckForTwoSeconds() => ViewSession.RunAsync(async () =>
    {
        MarkdownViewer viewer = new() { Text = "```\nlet x = 1;\n```" };
        Window window = ViewSession.Show(viewer);
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);
        await ViewSession.WaitForAsync(() => ViewSession.All<Button>(viewer).Any(button => button.Name == "PART_CopyButton"));
        Button copy = ViewSession.All<Button>(viewer).Single(button => button.Name == "PART_CopyButton");
        global::Avalonia.Controls.Shapes.Path glyph = Assert.IsType<global::Avalonia.Controls.Shapes.Path>(Assert.IsType<Viewbox>(copy.Content).Child);
        Geometry? copyGlyph = glyph.Data;

        copies.Refuse = true;
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ViewSession.WaitForAsync(() => copies.Requests.Count == 1);
        ViewSession.Settle();
        Assert.Same(copyGlyph, glyph.Data);
        Assert.NotEqual("Copied", AutomationProperties.GetName(copy));

        copies.Refuse = false;
        Stopwatch shown = Stopwatch.StartNew();
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ViewSession.WaitForAsync(() => AutomationProperties.GetName(copy) == "Copied");
        Assert.Equal("let x = 1;", await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
        Assert.NotSame(copyGlyph, glyph.Data);

        await ViewSession.WaitForAsync(() => AutomationProperties.GetName(copy) == "Copy");
        Assert.True(shown.Elapsed >= TimeSpan.FromSeconds(1.9), $"The check went after {shown.Elapsed}.");
        Assert.Same(copyGlyph, glyph.Data);
    });

    /// <summary>
    /// With a link source, a code span and plain text the source accepts become links, also while text streams in, and
    /// a Markdown link to a file the source refuses shows as plain text. A click activates an accepted target.
    /// </summary>
    [Fact]
    public Task ALinkSourceLinksAcceptedCodeSpansAndTextAndRefusesUnknownFiles() => ViewSession.RunAsync(async () =>
    {
        const string First = "Changed Program.cs and `Makefile`, ";
        const string Rest = "see [notes](missing-notes.md), [main](src/App/Main.cs), Design_DSA-1091_v_9.md and https://example.com/a.";
        MarkdownViewer viewer = new() { Streaming = true, LinkSource = new KnownFiles("Program.cs", "Makefile", "src/App/Main.cs", "Design_DSA-1091_v_9.md") };
        ViewSession.Show(viewer);
        List<string> activated = [];
        viewer.LinkActivated += (_, args) => activated.Add(args.Target);

        viewer.Text = First;
        await ViewSession.WaitForAsync(() => Links(viewer).Count == 2);
        viewer.Text = First + Rest;
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("example.com/a.", StringComparison.Ordinal) && Links(viewer).Count == 5);

        Assert.Equal(
            [("Program.cs", "Program.cs"), ("Makefile", "Makefile"), ("main", "src/App/Main.cs"), ("Design_DSA-1091_v_9.md", "Design_DSA-1091_v_9.md"),
                ("https://example.com/a", "https://example.com/a")],
            Links(viewer).Select(link => (TextOf(link), link.HRef?.OriginalString)));
        Assert.Contains("see notes, main", Text(viewer), StringComparison.Ordinal);

        MarkdownRenderer renderer = Assert.Single(ViewSession.All<MarkdownRenderer>(viewer));
        Link program = Links(viewer)[0];
        renderer.LinkCommand!.Execute(new LinkClickedEventArgs(MarkdownTextBlock.LinkClickEvent, program, program.HRef));
        renderer.LinkCommand.Execute(new Uri("missing-notes.md", UriKind.Relative));
        Assert.Equal(["Program.cs"], activated);
    });

    /// <summary>
    /// DSA-1270: a link source change that changes no link leaves the shown text as it is drawn. A long transcript's file
    /// index changes once for every file it names, and each change drew every message again on the UI thread.
    /// </summary>
    [Fact]
    public Task ALinkSourceChangeThatChangesNoLinkDrawsNothingAgain() => ViewSession.RunAsync(async () =>
    {
        ChangingFiles source = new() { Known = "Program.cs" };
        MarkdownViewer viewer = new() { LinkSource = source, Text = "Changed Program.cs today." };
        ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => Links(viewer).Count == 1);
        MarkdownRenderer renderer = Assert.Single(ViewSession.All<MarkdownRenderer>(viewer));
        int drawn = 0;
        renderer.PropertyChanged += (_, change) =>
        {
            if (change.Property == MarkdownRenderer.DocumentUpdateProperty) drawn++;
        };
        int searched = source.Searches;

        source.Raise();
        await ViewSession.WaitForAsync(() => source.Searches > searched);
        await Task.Delay(500);
        ViewSession.Settle();

        Assert.Equal(0, drawn);
        Assert.Single(Links(viewer));
    });

    /// <summary>A link source change that adds a link shows it.</summary>
    [Fact]
    public Task ALinkSourceChangeThatAddsALinkShowsIt() => ViewSession.RunAsync(async () =>
    {
        ChangingFiles source = new();
        MarkdownViewer viewer = new() { LinkSource = source, Text = "Changed Program.cs today." };
        ViewSession.Show(viewer);
        await ViewSession.WaitForAsync(() => Text(viewer).Contains("today", StringComparison.Ordinal));
        Assert.Empty(Links(viewer));

        source.Known = "Program.cs";
        source.Raise();

        await ViewSession.WaitForAsync(() => Links(viewer).Count == 1);
    });

    /// <summary>Without the host's link resources a link keeps the accent colour and no underline.</summary>
    [Fact]
    public Task WithoutLinkResourcesALinkIsAccentColouredAndNotUnderlined() => ViewSession.RunAsync(async () =>
    {
        Link link = await ShowLinkAsync(host => host.Resources["accent"] = Colors.Green);

        Assert.Equal(Colors.Green, ((ISolidColorBrush)link.Foreground!).Color);
        Assert.False(Underlined(link));
    });

    /// <summary>The host's "link" colour colours a link, and its "link-underline" decides whether the link is underlined.</summary>
    [Fact]
    public Task ALinkTakesTheHostsLinkColourAndDecoration() => ViewSession.RunAsync(async () =>
    {
        Link underlined = await ShowLinkAsync(host =>
        {
            host.Resources["accent"] = Colors.Green;
            host.Resources["link"] = Colors.Red;
            host.Resources["link-underline"] = true;
        });
        Link plain = await ShowLinkAsync(host =>
        {
            host.Resources["accent"] = Colors.Green;
            host.Resources["link"] = Colors.Red;
            host.Resources["link-underline"] = false;
        });

        Assert.Equal(Colors.Red, ((ISolidColorBrush)underlined.Foreground!).Color);
        Assert.True(Underlined(underlined));
        Assert.Equal(Colors.Red, ((ISolidColorBrush)plain.Foreground!).Color);
        Assert.False(Underlined(plain));
    });

    private static async Task<Link> ShowLinkAsync(Action<Border> resources)
    {
        MarkdownViewer viewer = new() { Text = "See [the site](https://example.com/a)." };
        Border host = new() { Child = viewer };
        resources(host);
        ViewSession.Show(host);
        await ViewSession.WaitForAsync(() => Links(viewer).Count == 1);
        return Links(viewer)[0];
    }

    private static bool Underlined(Link link) => link.TextDecorations?.Any(decoration => decoration.Location == TextDecorationLocation.Underline) == true;

    /// <summary>A link source that knows a fixed set of file names and finds each where it appears in the text.</summary>
    private sealed class KnownFiles(params string[] names) : IMarkdownLinkSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<MarkdownLinkSpan> Find(string text) =>
        [
            .. names.SelectMany(name => Occurrences(text, name).Select(start => new MarkdownLinkSpan(start, name.Length, name)))
                .OrderBy(span => span.Start),
        ];

        public bool Accepts(string target) => names.Contains(target);

        private static IEnumerable<int> Occurrences(string text, string name)
        {
            for (int start = text.IndexOf(name, StringComparison.Ordinal); start >= 0; start = text.IndexOf(name, start + name.Length, StringComparison.Ordinal))
                yield return start;
        }
    }

    /// <summary>A link source that links one known name once it is set, and counts its searches.</summary>
    private sealed class ChangingFiles : IMarkdownLinkSource
    {
        private volatile string? known;
        private int searches;

        public event EventHandler? Changed;

        public string? Known { set => this.known = value; }

        public int Searches => Volatile.Read(ref this.searches);

        public IReadOnlyList<MarkdownLinkSpan> Find(string text)
        {
            Interlocked.Increment(ref this.searches);
            if (this.known is not { } name) return [];
            int start = text.IndexOf(name, StringComparison.Ordinal);
            return start < 0 ? [] : [new(start, name.Length, name)];
        }

        public bool Accepts(string target) => target == this.known;

        public void Raise() => this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<Link> Links(MarkdownViewer viewer) => [.. ViewSession.All<MarkdownTextBlock>(viewer).SelectMany(block => LinksIn(block.Inlines))];

    private static IEnumerable<Link> LinksIn(InlineCollection? inlines) => inlines is null ? [] : inlines.SelectMany(inline => inline switch
    {
        Link link => [link, .. LinksIn(link.Inlines)],
        Span span => LinksIn(span.Inlines),
        _ => [],
    });

    private static string TextOf(global::Avalonia.Controls.Documents.Inline inline) => inline switch
    {
        Run run => run.Text ?? string.Empty,
        Span span => string.Concat(span.Inlines.Select(TextOf)),
        _ => string.Empty,
    };

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
