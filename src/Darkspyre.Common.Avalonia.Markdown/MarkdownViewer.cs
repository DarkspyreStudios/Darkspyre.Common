using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Extensions.MediaLinks;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CodeBlock = LiveMarkdown.Avalonia.CodeBlock;
using CodeInline = LiveMarkdown.Avalonia.CodeInline;

namespace Darkspyre.Common.Avalonia.Markdown;

/// <summary>Raised when the person activates a link the viewer allows.</summary>
/// <param name="target">The link's target as the Markdown wrote it.</param>
public sealed class MarkdownLinkEventArgs(string target) : EventArgs
{
    /// <summary>The link's target as the Markdown wrote it: an http, https or mailto address, or a relative path.</summary>
    public string Target { get; } = target;
}

/// <summary>
/// Renders Markdown, including Mermaid diagrams. Raw HTML is shown as text; links outside the allow-list become inert.
/// </summary>
/// <remarks>
/// The allow-list is absolute http and https addresses with a host, mailto addresses, and relative paths. Generic
/// attributes and media links are off, so Markdown cannot add attributes or embed players. Code blocks carry a copy
/// button and no syntax colouring. A <c>mermaid</c> fence is drawn by <see cref="MermaidRenderer"/>; when that draws
/// nothing, the fence stays a code block. Colours come from the theme token resources and follow them when they change:
/// inline code has no fill of its own and its text is <c>code-inline</c>, code blocks and quotes sit on <c>raised</c>, code
/// block text is <c>text</c>, a code block's header and border, table
/// rules and thematic breaks are <c>line</c>, tables sit on <c>panel</c>, links and the quote rule are <c>accent</c>, selected
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

    internal static readonly MarkdownPipeline Pipeline = CreatePipeline();

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

    private readonly MarkdownRenderer renderer = new();
    private readonly ObservableStringBuilder builder = new();
    private string shown = string.Empty;
    private (string Key, Color Colour)[] palette = [];

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
        this.Styles.Add(new Style(selector => selector.OfType<MarkdownRenderer>().Descendant().OfType<Link>())
        {
            Setters = { new Setter(Link.ForegroundProperty, new DynamicResourceExtension("accent.brush")) },
            Children = { new Style(selector => selector.Nesting().Class(":clicked")) { Setters = { new Setter(Link.ForegroundProperty, new DynamicResourceExtension("accent.brush")) } } },
        });
        this.renderer.UpdateProducer = new MarkdownUpdateProducer { Pipeline = Pipeline, MarkdownBuilder = this.builder };
        this.renderer.LinkCommand = new LinkActivation(this.Activate);
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

    /// <summary>Raised when the user activates an allowed link.</summary>
    public event EventHandler<MarkdownLinkEventArgs>? LinkActivated;

    /// <summary>Raised when the theme variant or a theme resource changes, so diagrams are drawn again.</summary>
    internal event EventHandler? ThemeChanged;

    /// <summary>Whether a link to <paramref name="target"/> may be activated.</summary>
    public static bool IsAllowedTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target != target.Trim() || target.StartsWith("//", StringComparison.Ordinal)
            || target.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '\\'))
            return false;
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute))
            return absolute.Scheme is "http" or "https" or "mailto" && (absolute.Scheme == "mailto" || !string.IsNullOrEmpty(absolute.Host));
        return Uri.TryCreate(target, UriKind.Relative, out _);
    }

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
        return new(Find("panel", dark ? Color.Parse("#1e1e1e") : Colors.White), Find("text", dark ? Color.Parse("#e4e4e7") : Color.Parse("#27272a")),
            Find("accent", Color.Parse("#3b82f6")), Find("line", dark ? Color.Parse("#3f3f46") : Color.Parse("#d4d4d8")), font);
    }

    // The renderer's own resources shadow the defaults merged above, so a token the theme lacks keeps the default. The
    // palette is read whole and given to the renderer as one dictionary only when a colour differs: each write to the
    // renderer's resources restyles everything it shows, and a resource change elsewhere in the window changes none.
    private void ApplyPalette()
    {
        List<(string Key, Color Colour)> next = [];
        foreach ((string key, string token) in Palette)
            if (this.TryFindResource(token, this.ActualThemeVariant, out object? value) && value is Color colour) next.Add((key, colour));
        if (next.SequenceEqual(this.palette)) return;
        this.palette = [.. next];
        ResourceDictionary resources = [];
        foreach ((string key, Color colour) in next) resources[key] = colour;
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
            this.renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(text, Pipeline), this.builder.Version);
        }
        this.shown = text;
    }

    private void Activate(object? parameter)
    {
        string? target = parameter switch
        {
            Uri uri => uri.OriginalString,
            Link link => link.HRef?.OriginalString,
            string text => text,
            _ => null,
        };
        if (IsAllowedTarget(target)) this.LinkActivated?.Invoke(this, new MarkdownLinkEventArgs(target!));
    }

    private static MarkdownPipeline CreatePipeline()
    {
        MarkdownPipelineBuilder pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // The advanced preset includes arbitrary attributes and unsandboxed media embeds.
        pipeline.Extensions.TryRemove<GenericAttributesExtension>();
        pipeline.Extensions.TryRemove<MediaLinkExtension>();
        pipeline.Extensions.AddIfNotAlready<MermaidFences>();
        pipeline.Extensions.AddIfNotAlready<InertLinks>();
        return pipeline.DisableHtml().UseCodeBlockSpanFixer().Build();
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

    /// <summary>Empties the target of every link and image the allow-list refuses, so it cannot be followed or loaded.</summary>
    private sealed class InertLinks : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Guard;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }

        private static void Guard(MarkdownDocument document)
        {
            foreach (LinkInline link in document.Descendants<LinkInline>())
            {
                string? target = link.GetDynamicUrl?.Invoke() ?? link.Url;
                link.Url = IsAllowedTarget(target) ? target : string.Empty;
                link.GetDynamicUrl = null;
            }
            foreach (AutolinkInline link in document.Descendants<AutolinkInline>())
            {
                if (IsAllowedTarget(link.IsEmail ? "mailto:" + link.Url : link.Url)) continue;
                link.Url = string.Empty;
                link.IsEmail = false;
            }
        }
    }
}
