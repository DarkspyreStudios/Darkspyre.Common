using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Common.Markdown;
using Darkspyre.Common.Markdown.Mermaid;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CodeBlock = LiveMarkdown.Avalonia.CodeBlock;
using CodeInline = LiveMarkdown.Avalonia.CodeInline;

namespace Darkspyre.Agent.Common.UI.Markdown;

/// <summary>Raised when the person activates a link the viewer allows.</summary>
/// <param name="target">The link's target as the Markdown wrote it.</param>
public sealed class MarkdownLinkEventArgs(string target) : EventArgs
{
    /// <summary>The link's target: an http, https or mailto address, a relative path, or a target the viewer's
    /// <see cref="MarkdownViewer.LinkSource"/> accepts.</summary>
    public string Target { get; } = target;
}

/// <summary>
/// Renders Markdown, including Mermaid diagrams. Raw HTML is shown as text; links outside the allow-list become inert.
/// </summary>
/// <remarks>
/// Links, attributes, media and raw HTML follow <see cref="MarkdownSafety"/>. With a <see cref="LinkSource"/>, the
/// source also adds links to plain text and code spans. Code blocks carry a copy
/// button that copies through <see cref="TextCopy"/> and shows a check for two seconds after the copy, and no syntax
/// colouring. Selection, the right-click menu and the Copy and Select All keys come from the enclosing
/// <see cref="TextSelectionScope"/>; a right-click on the text requests that menu. A <c>mermaid</c> fence is drawn by
/// <see cref="MermaidRenderer"/>; when that draws nothing, the fence stays a code block. Colours come from the theme token resources and follow them when they change:
/// inline code has no fill of its own and its text is <c>code-inline</c>, code blocks and quotes sit on <c>raised</c>, code
/// block text is <c>text</c>, a code block's header and border, table
/// rules and thematic breaks are <c>line</c>, tables sit on <c>panel</c>, the quote rule is <c>accent</c>, links are <c>link</c> (else <c>accent</c>), underlined when <c>link-underline</c> is true, selected
/// text is highlighted in <c>color-primary.brush.40</c> as the app's text controls are, and diagrams are drawn again in
/// <c>panel</c>, <c>text</c>, <c>accent</c> and <c>line</c>. A colour token the resources lack keeps LiveMarkdown's default
/// colour. Fonts come from the text entry resources and follow them when they change: paragraphs, quotes, list items,
/// table cells and list markers are <c>font-body</c> at <c>font-size-body</c>, a heading of level N is
/// <c>font-heading-N</c> at <c>font-size-heading-N</c>, and inline code and code blocks are <c>font-code</c> at
/// <c>font-size-code</c>. These replace the fixed fonts LiveMarkdown's own styles set.
/// </remarks>
public sealed class MarkdownViewer : TemplatedControl
{
    /// <summary>The Markdown shown.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<MarkdownViewer, string?>(nameof(Text));

    /// <summary>True while text is still arriving.</summary>
    public static readonly StyledProperty<bool> StreamingProperty = AvaloniaProperty.Register<MarkdownViewer, bool>(nameof(Streaming));

    /// <summary>The renderer for mermaid fences.</summary>
    public static readonly StyledProperty<IMermaidRenderer?> MermaidRendererProperty =
        AvaloniaProperty.Register<MarkdownViewer, IMermaidRenderer?>(nameof(MermaidRenderer), new MermaiderRenderer());

    internal static readonly MarkdownPipeline Pipeline = CreatePipeline(null);

    // LiveMarkdown's colour resources, and the theme token each one follows.
    private static readonly (string Key, string Token)[] Palette =
    [
        ("ForegroundColor", "text"),
        ("CodeInlineColor", "code-inline"),
        ("SecondaryCardBackgroundColor", "raised"),
        ("BorderColor", "line"),
        ("BorderBrush", "line"),
        ("CardBackgroundColor", "panel"),
        ("QuoteBorderColor", "accent"),
    ];

    // The renderer's own resources for the link look: the colour of the host's "link" (else "accent") token as a brush, and
    // the underline when the host's "link-underline" is true. The viewer makes the decoration itself on the UI thread, since
    // a TextDecorationCollection belongs to the thread that made it and a host may build its resources on another.
    private const string LinkBrushKey = "darkspyre-link-brush";
    private const string LinkDecorationsKey = "darkspyre-link-decorations";
    private static readonly string[] LinkTokens = ["link", "accent"];

    private readonly MarkdownRenderer renderer = new();
    private readonly ObservableStringBuilder builder = new();
    private readonly MarkdownUpdateProducer producer;
    private MarkdownPipeline pipeline = Pipeline;
    private IMarkdownLinkSource? linkSource;
    private string shown = string.Empty;
    private (string Key, Color Colour)[] palette = [];
    private bool linkUnderline;
    // Whether a relink is parsing, and whether the link source changed again meanwhile. Used only on the UI thread.
    private bool relinking;
    private bool relinkAgain;

    static MarkdownViewer() => MarkdownNode.Register<MermaidNode>();

    /// <summary>Creates the viewer.</summary>
    public MarkdownViewer()
    {
        this.Styles.Add(new StyleInclude(new Uri("avares://LiveMarkdown.Avalonia/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml") });
        this.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://LiveMarkdown.Avalonia/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Defaults.axaml") });
        this.Styles.Add(new Style(selector => selector.OfType<CodeBlock>())
        {
            Setters =
            {
                new Setter(CodeBlock.AutoSyntaxHighlightProperty, false), new Setter(CodeBlock.FontFamilyProperty, new DynamicResourceExtension("font-code")),
                new Setter(CodeBlock.FontSizeProperty, new DynamicResourceExtension("font-size-code")),
            },
        });
        this.Styles.Add(new Style(selector => selector.OfType<MarkdownRenderer>().Descendant().OfType<CodeInline>())
        {
            Setters =
            {
                new Setter(CodeInline.FontFamilyProperty, new DynamicResourceExtension("font-code")),
                new Setter(CodeInline.FontSizeProperty, new DynamicResourceExtension("font-size-code")), new Setter(CodeInline.BackgroundProperty, Brushes.Transparent),
            },
        });
        this.Styles.Add(new Style(selector => selector.OfType<MarkdownTextBlock>())
        {
            Setters =
            {
                new Setter(SelectableTextBlock.SelectionBrushProperty, new DynamicResourceExtension("color-primary.brush.40")),
                new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("font-body")),
                new Setter(TextBlock.FontSizeProperty, new DynamicResourceExtension("font-size-body")),
            },
        });
        // LiveMarkdown classes each heading's text block Heading1 to Heading6, and its list markers ListBlockBullet and
        // ListBlockNumber; these follow the shared text block style so they win over it.
        for (int level = 1; level <= 6; level++)
        {
            string heading = "Heading" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string entry = "heading-" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            this.Styles.Add(new Style(selector => selector.OfType<MarkdownTextBlock>().Class(heading))
            {
                Setters =
                {
                    new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("font-" + entry)),
                    new Setter(TextBlock.FontSizeProperty, new DynamicResourceExtension("font-size-" + entry)),
                },
            });
        }
        this.Styles.Add(new Style(selector => Selectors.Or(selector.OfType<TextBlock>().Class("ListBlockBullet"), selector.OfType<TextBlock>().Class("ListBlockNumber")))
        {
            Setters =
            {
                new Setter(TextBlock.FontFamilyProperty, new DynamicResourceExtension("font-body")),
                new Setter(TextBlock.FontSizeProperty, new DynamicResourceExtension("font-size-body")),
            },
        });
        // The renderer's resources carry the link look ApplyPalette read from the host.
        this.Styles.Add(new Style(selector => selector.OfType<MarkdownRenderer>().Descendant().OfType<Link>())
        {
            Setters =
            {
                new Setter(Link.ForegroundProperty, new DynamicResourceExtension(LinkBrushKey)),
                new Setter(Link.TextDecorationsProperty, new DynamicResourceExtension(LinkDecorationsKey)),
            },
            Children = { new Style(selector => selector.Nesting().Class(":clicked")) { Setters = { new Setter(Link.ForegroundProperty, new DynamicResourceExtension(LinkBrushKey)) } } },
        });
        this.producer = new MarkdownUpdateProducer { Pipeline = Pipeline, MarkdownBuilder = this.builder };
        this.renderer.UpdateProducer = this.producer;
        this.renderer.LinkCommand = new LinkActivation(this.Activate);
        this.AddHandler(PointerReleasedEvent, this.RequestContext);
        this.AddHandler(CodeBlock.CopyingToClipboardEvent, this.CopyCode);
        this.Template = new FuncControlTemplate<MarkdownViewer>((viewer, _) => viewer.renderer);
        this.ActualThemeVariantChanged += (_, _) =>
        {
            this.ApplyPalette();
            this.ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
        this.ResourcesChanged += (_, _) =>
        {
            this.ApplyPalette();
            this.ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
        this.ApplyPalette();
    }

    /// <summary>The Markdown shown, or null for none.</summary>
    public string? Text
    {
        get => this.GetValue(TextProperty);
        set => this.SetValue(TextProperty, value);
    }

    /// <summary>True while text is still arriving; only the changed tail is re-rendered.</summary>
    public bool Streaming
    {
        get => this.GetValue(StreamingProperty);
        set => this.SetValue(StreamingProperty, value);
    }

    /// <summary>Renderer for mermaid fences. Defaults to <see cref="MermaiderRenderer"/>; null shows every fence as code.</summary>
    public IMermaidRenderer? MermaidRenderer
    {
        get => this.GetValue(MermaidRendererProperty);
        set => this.SetValue(MermaidRendererProperty, value);
    }

    /// <summary>Finds extra links in the text; null finds none. Setting it reparses the whole text, streamed text included.</summary>
    /// <remarks>
    /// While a source is set, an http, https or mailto target is still checked by <see cref="MarkdownSafety.IsAllowedTarget"/>, and
    /// every other target is a link only when <see cref="IMarkdownLinkSource.Accepts"/> is true. When the source raises
    /// <see cref="IMarkdownLinkSource.Changed"/>, the whole text is parsed again on a worker thread. The renderer draws the
    /// result on the UI thread only when its links differ from the drawn document's and that text is still the one shown.
    /// Changes that arrive during that parse cause one more.
    /// </remarks>
    public IMarkdownLinkSource? LinkSource
    {
        get => this.linkSource;
        set
        {
            if (ReferenceEquals(this.linkSource, value)) return;
            if (this.linkSource is not null) this.linkSource.Changed -= this.LinkSourceChanged;
            this.linkSource = value;
            if (value is not null) value.Changed += this.LinkSourceChanged;
            this.Reparse();
        }
    }

    /// <summary>Raised when the user activates an allowed link.</summary>
    public event EventHandler<MarkdownLinkEventArgs>? LinkActivated;

    /// <summary>Raised when the theme variant or a theme resource changes, so diagrams are drawn again.</summary>
    internal event EventHandler? ThemeChanged;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) this.Show(this.Text ?? string.Empty);
        else if (change.Property == MermaidRendererProperty) this.ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The diagram colours the current theme resources give, with light or dark defaults where one is missing.</summary>
    internal MermaidTheme CurrentMermaidTheme()
    {
        bool dark = this.ActualThemeVariant == ThemeVariant.Dark;
        Color Find(string key, Color fallback) => this.TryFindResource(key, this.ActualThemeVariant, out object? found) && found is Color colour ? colour : fallback;
        string font = this.TryFindResource("font-body", this.ActualThemeVariant, out object? family) && family is FontFamily named && named.FamilyNames.Count > 0
            ? named.FamilyNames[0] : FontFamily.Default.Name;
        return new(Hex(Find("panel", dark ? Color.Parse("#1e1e1e") : Colors.White)), Hex(Find("text", dark ? Color.Parse("#e4e4e7") : Color.Parse("#27272a"))),
            Hex(Find("line", dark ? Color.Parse("#3f3f46") : Color.Parse("#d4d4d8"))), Hex(Find("accent", Color.Parse("#3b82f6"))), font);
    }

    private static string Hex(Color colour) => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}");

    // The renderer's own resources shadow the defaults merged above, so a token the theme lacks keeps the default. The
    // palette is read whole and given to the renderer as one dictionary only when a colour differs: each write to the
    // renderer's resources restyles everything it shows, and a resource change elsewhere in the window changes none.
    private void ApplyPalette()
    {
        List<(string Key, Color Colour)> next = [];
        foreach ((string key, string token) in Palette)
            if (this.TryFindResource(token, this.ActualThemeVariant, out object? value) && value is Color colour) next.Add((key, colour));
        foreach (string token in LinkTokens)
        {
            if (!this.TryFindResource(token, this.ActualThemeVariant, out object? value) || value is not Color colour) continue;
            next.Add((LinkBrushKey, colour));
            break;
        }
        bool underline = this.TryFindResource("link-underline", this.ActualThemeVariant, out object? found) && found is true;
        if (next.SequenceEqual(this.palette) && underline == this.linkUnderline) return;
        this.palette = [.. next];
        this.linkUnderline = underline;
        ResourceDictionary resources = [];
        foreach ((string key, Color colour) in next) resources[key] = key == LinkBrushKey ? new global::Avalonia.Media.Immutable.ImmutableSolidColorBrush(colour) : colour;
        if (underline) resources[LinkDecorationsKey] = TextDecorations.Underline;
        this.renderer.Resources = resources;
    }

    // A streamed reply only grows, so its new tail is appended; any other change replaces the text. The producer applies
    // a replacement in a later dispatcher job, so a new or replaced viewer would be laid out at its old height for one
    // frame and the transcript would jump; a replacement is therefore also parsed and given to the renderer at once.
    private void Show(string text)
    {
        if (text == this.shown) return;
        if (this.Streaming && text.StartsWith(this.shown, StringComparison.Ordinal)) this.builder.Append(text[this.shown.Length..]);
        else
        {
            this.builder.Clear();
            this.builder.Append(text);
            this.renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(text, this.pipeline), this.builder.Version);
        }
        this.shown = text;
    }

    // A new pipeline makes the producer parse the whole text again, streamed text included.
    private void Reparse()
    {
        this.pipeline = this.linkSource is null ? Pipeline : CreatePipeline(this.linkSource);
        this.producer.Pipeline = this.pipeline;
    }

    private void LinkSourceChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!ReferenceEquals(sender, this.linkSource)) return;
        if (this.relinking) this.relinkAgain = true;
        else _ = this.RelinkAsync();
    });

    // A link source can change once for every file a long transcript names, and most changes change no link in this text.
    // The parse and the comparison with the drawn document run off the UI thread, and the UI thread draws only a document
    // whose links changed. The pipeline stays the same: its link extension asks the source again on every parse.
    private async Task RelinkAsync()
    {
        this.relinking = true;
        try
        {
            do
            {
                this.relinkAgain = false;
                string text = this.shown;
                long version = this.builder.Version;
                MarkdownPipeline pipeline = this.pipeline;
                MarkdownDocument? drawn = this.renderer.DocumentUpdate?.Document;
                MarkdownDocument? document;
                try { document = await Task.Run(() => Relinked(text, pipeline, drawn)); }
                catch (Exception error) when (error is not OutOfMemoryException) { continue; }
                if (document is not null && text == this.shown && version == this.builder.Version && ReferenceEquals(pipeline, this.pipeline))
                    this.renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(document, version);
            }
            while (this.relinkAgain);
        }
        finally { this.relinking = false; }
    }

    // The text parsed again, or null when it has the drawn document's links. A published document is never changed, so
    // this reads the drawn one off the UI thread.
    private static MarkdownDocument? Relinked(string text, MarkdownPipeline pipeline, MarkdownDocument? drawn)
    {
        MarkdownDocument document = Markdig.Markdown.Parse(text, pipeline);
        return drawn is not null && LinksOf(document).SequenceEqual(LinksOf(drawn)) ? null : document;
    }

    // Every link of the document in order, with its source span and target.
    private static IEnumerable<(int Start, int End, string? Url)> LinksOf(MarkdownDocument document) =>
        document.Descendants<LeafBlock>().SelectMany(block => block.Inline is { } inline ? LinksIn(inline) : []);

    private static IEnumerable<(int Start, int End, string? Url)> LinksIn(ContainerInline container)
    {
        for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LinkInline link) yield return (link.Span.Start, link.Span.End, link.Url);
            if (child is ContainerInline nested)
                foreach ((int Start, int End, string? Url) inner in LinksIn(nested)) yield return inner;
        }
    }

    // Avalonia raises ContextRequested for a right-click only from the control under the pointer, in that control's own
    // pointer-released handling. LiveMarkdown's text block ends that handling early inside a renderer, so a right-click on
    // the text requests no menu. A right release that reaches the viewer unhandled raises the request from the viewer.
    private void RequestContext(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Right) return;
        ContextRequestedEventArgs request = new(e);
        this.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    // LiveMarkdown raises this before its code-block button copies and skips its own copy when it is handled.
    private void CopyCode(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not CodeBlock block) return;
        e.Handled = true;
        string code = block.Code ?? string.Empty;
        if (block.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "PART_CopyButton") is { } copy)
            _ = CopyFeedback.CopyAsync(copy, code);
        else _ = TextCopy.RequestAsync(block, code);
    }

    private void Activate(object? parameter)
    {
        string? target = parameter switch
        {
            Uri uri => uri.OriginalString,
            Link link => link.HRef?.OriginalString,
            LinkClickedEventArgs clicked => clicked.HRef?.OriginalString,
            string text => text,
            _ => null,
        };
        if (MarkdownSafety.Allows(this.linkSource, target)) this.LinkActivated?.Invoke(this, new MarkdownLinkEventArgs(target!));
    }

    private static MarkdownPipeline CreatePipeline(IMarkdownLinkSource? source)
    {
        MarkdownPipelineBuilder pipeline = MarkdownSafety.CreateBuilder(source);
        pipeline.Extensions.AddIfNotAlready<MermaidFences>();
        if (source is not null) pipeline.Extensions.Add(new FileLinks(source));
        return MarkdownSafety.Build(pipeline.UseCodeBlockSpanFixer());
    }

    /// <summary>Runs the viewer's link check for the link the renderer reports.</summary>
    private sealed class LinkActivation(Action<object?> activate) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => activate(parameter);
    }

    /// <summary>
    /// Makes links the source finds: in plain text outside links, each span <see cref="IMarkdownLinkSource.Find"/>
    /// returns, and a code span whose whole trimmed text the source accepts. Adjacent literals are searched as one text,
    /// because Markdig splits text at characters such as an unmatched underscore.
    /// </summary>
    private sealed class FileLinks(IMarkdownLinkSource source) : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += this.Link;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }

        private void Link(MarkdownDocument document)
        {
            foreach (LeafBlock block in document.Descendants<LeafBlock>().ToList())
                if (block.Inline is { } inline) this.Walk(inline);
        }

        private void Walk(ContainerInline container)
        {
            Inline? child = container.FirstChild;
            while (child is not null)
            {
                switch (child)
                {
                    case LinkInline:
                        child = child.NextSibling;
                        break;
                    case ContainerInline nested:
                        this.Walk(nested);
                        child = child.NextSibling;
                        break;
                    case Markdig.Syntax.Inlines.CodeInline code:
                        child = code.NextSibling;
                        this.LinkCode(code);
                        break;
                    case LiteralInline literal:
                        List<LiteralInline> run = [literal];
                        while (run[^1].NextSibling is LiteralInline next) run.Add(next);
                        child = run[^1].NextSibling;
                        this.LinkText(run);
                        break;
                    default:
                        child = child.NextSibling;
                        break;
                }
            }
        }

        private void LinkCode(Markdig.Syntax.Inlines.CodeInline code)
        {
            string target = code.Content.Trim();
            if (target.Length == 0 || MarkdownSafety.IsWebTarget(target) || !source.Accepts(target)) return;
            LinkInline link = new(target, string.Empty) { Span = code.Span, IsClosed = true };
            code.ReplaceBy(link);
            link.AppendChild(code);
        }

        private void LinkText(List<LiteralInline> run)
        {
            string text = string.Concat(run.Select(literal => literal.Content.ToString()));
            IReadOnlyList<MarkdownLinkSpan> spans = source.Find(text);
            if (spans.Count == 0) return;
            // Where each literal starts in the joined text, to give every new inline a source span.
            int[] starts = new int[run.Count];
            for (int index = 1; index < run.Count; index++) starts[index] = starts[index - 1] + run[index - 1].Content.Length;
            SourceSpan SpanOf(int start, int end)
            {
                int Source(int offset)
                {
                    int index = Array.FindLastIndex(starts, begin => begin <= offset);
                    SourceSpan span = run[index].Span;
                    return Math.Min(span.Start + offset - starts[index], span.End);
                }
                return new SourceSpan(Source(start), Source(end - 1));
            }
            LiteralInline Literal(int start, int end) => new(text[start..end]) { Span = SpanOf(start, end) };

            Inline first = run[0];
            int position = 0;
            foreach (MarkdownLinkSpan found in spans)
            {
                if (found.Start > position) first.InsertBefore(Literal(position, found.Start));
                LinkInline link = new(found.Target, string.Empty) { Span = SpanOf(found.Start, found.Start + found.Length), IsClosed = true };
                link.AppendChild(Literal(found.Start, found.Start + found.Length));
                first.InsertBefore(link);
                position = found.Start + found.Length;
            }
            if (position < text.Length) first.InsertBefore(Literal(position, text.Length));
            foreach (LiteralInline literal in run) literal.Remove();
        }
    }
}
