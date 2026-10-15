using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
/// nothing, the fence stays a code block. Colours come from the theme token resources (<c>panel</c>, <c>text</c>,
/// <c>accent</c>, <c>line</c>) and diagrams are drawn again when they change.
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

    private readonly MarkdownRenderer renderer = new();
    private readonly ObservableStringBuilder builder = new();
    private string shown = string.Empty;

    static MarkdownViewer() => MarkdownNode.Register<MermaidNode>();

    /// <summary>Creates the viewer.</summary>
    public MarkdownViewer()
    {
        this.Styles.Add(new StyleInclude(new Uri("avares://LiveMarkdown.Avalonia/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Styles.axaml") });
        this.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://LiveMarkdown.Avalonia/")) { Source = new Uri("avares://LiveMarkdown.Avalonia/Defaults.axaml") });
        this.Styles.Add(new Style(selector => selector.OfType<CodeBlock>()) { Setters = { new Setter(CodeBlock.AutoSyntaxHighlightProperty, false) } });
        this.renderer.UpdateProducer = new MarkdownUpdateProducer { Pipeline = Pipeline, MarkdownBuilder = this.builder };
        this.renderer.LinkCommand = new LinkActivation(this.Activate);
        this.Template = new FuncControlTemplate<MarkdownViewer>((viewer, _) => viewer.renderer);
        this.ActualThemeVariantChanged += (_, _) => this.ThemeChanged?.Invoke(this, EventArgs.Empty);
        this.ResourcesChanged += (_, _) => this.ThemeChanged?.Invoke(this, EventArgs.Empty);
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
        string font = this.TryFindResource("font-ui", this.ActualThemeVariant, out object? family) && family is FontFamily named && named.FamilyNames.Count > 0
            ? named.FamilyNames[0] : FontFamily.Default.Name;
        return new(Find("panel", dark ? Color.Parse("#1e1e1e") : Colors.White), Find("text", dark ? Color.Parse("#e4e4e7") : Color.Parse("#27272a")),
            Find("accent", Color.Parse("#3b82f6")), Find("line", dark ? Color.Parse("#3f3f46") : Color.Parse("#d4d4d8")), font);
    }

    // A streamed reply only grows, so its new tail is appended; any other change replaces the text.
    private void Show(string text)
    {
        if (text == this.shown) return;
        if (this.Streaming && text.StartsWith(this.shown, StringComparison.Ordinal)) this.builder.Append(text[this.shown.Length..]);
        else
        {
            this.builder.Clear();
            this.builder.Append(text);
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
