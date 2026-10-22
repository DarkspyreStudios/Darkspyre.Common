using Darkspyre.Common.Markdown;
using Darkspyre.Common.Markdown.Mermaid;
using Markdig;
using Markdig.Syntax;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Darkspyre.Mobile.Components;

/// <summary>
/// Renders Markdown as HTML under the shared Markdown safety policy, colours code blocks in a language highlight.js knows, and
/// draws each closed <c>mermaid</c> fence as a diagram image.
/// </summary>
/// <remarks>
/// Only http, https and mailto targets stay links; every other link or autolink target, relative ones included, shows as
/// its own text. Every image shows as its alt text, so no remote image loads. Raw HTML shows as text, and no attribute,
/// style or embed reaches the page. After each render the page's <c>highlightNew</c> function from <c>markdown.js</c>
/// colours the code blocks not yet coloured; a block in an unknown language stays plain text. The host page loads
/// <c>_content/Darkspyre.Mobile.Components/highlight/highlight.min.js</c> and
/// <c>_content/Darkspyre.Mobile.Components/markdown.js</c>.
/// A closed <c>mermaid</c> fence shows as code until it is drawn, and stays code when it cannot be. After a render, the page's
/// <c>readMermaidTheme</c> function reads the <c>--panel</c>, <c>--text</c>, <c>--line</c>, <c>--accent</c> and <c>--font-body</c>
/// values of <c>mobile.css</c> as the diagram theme, each closed fence is drawn on the thread pool, and the view renders again with
/// an <c>img</c> element whose <c>src</c> is the SVG as a base64 data address. The SVG never enters the page as markup. Drawn
/// diagrams are kept by fence source and theme for the life of the app, so a streamed reply draws only its new fences.
/// </remarks>
/// <gotcha>A tapped link navigates the web view unless the host keeps it on the app's own pages; a MAUI host calls
/// <c>UseDarkspyreLinkPolicy</c> from Darkspyre.Mobile.Maui.</gotcha>
public sealed partial class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static readonly MermaidImages Diagrams = new(new MermaiderRenderer());

    private ElementReference element;
    private string html = string.Empty;
    private string? parsed;
    private IReadOnlyList<string> fences = [];
    private MermaidTheme? theme;

    /// <summary>The Markdown shown, or null for none.</summary>
    [Parameter]
    public string? Text { get; set; }

    [Inject]
    private IJSRuntime Script { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        string text = this.Text ?? string.Empty;
        if (text == this.parsed) return;
        this.parsed = text;
        this.Build();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            await this.Script.InvokeVoidAsync("highlightNew", this.element);
        }
        catch (JSException)
        {
            // The page has no highlightNew, or it failed: the code blocks stay plain text.
        }
        catch (JSDisconnectedException)
        {
            // The page is gone, so nothing is left to colour.
        }
        if (this.fences.Count == 0) return;
        MermaidTheme? current = await this.ReadThemeAsync();
        if (current is null || (current == this.theme && Diagrams.IsSettled(this.fences, current))) return;
        this.theme = current;
        await Diagrams.DrawAsync(this.fences, current);
        this.Build();
        this.StateHasChanged();
    }

    private void Build()
    {
        MarkdownDocument document = Markdig.Markdown.Parse(this.parsed ?? string.Empty, Pipeline);
        this.fences = MermaidImages.FencesIn(document);
        if (this.theme is { } known) Diagrams.Apply(document, known);
        this.html = document.ToHtml(Pipeline);
    }

    // The theme is whatever the page's stylesheet gives now, so a change of the system appearance redraws the diagrams on the next
    // render. A page without readMermaidTheme, or a value that is not a colour, leaves every fence as code.
    private async Task<MermaidTheme?> ReadThemeAsync()
    {
        try
        {
            MermaidTheme? read = await this.Script.InvokeAsync<MermaidTheme?>("readMermaidTheme", this.element);
            if (read is null || !IsColour(read.Background) || !IsColour(read.Foreground) || !IsColour(read.Line) || !IsColour(read.Accent)) return null;
            string font = new([.. (read.FontFamily ?? string.Empty).Where(character => char.IsLetterOrDigit(character) || character is ' ' or ',' or '-' or '_')]);
            return read with { FontFamily = font.Trim().Length == 0 ? "sans-serif" : font };
        }
        catch (JSException)
        {
            return null;
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
    }

    private static bool IsColour(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static MarkdownPipeline CreatePipeline()
    {
        MarkdownPipelineBuilder builder = MarkdownSafety.CreateBuilder(NoLinkSource.Instance);
        builder.Extensions.Add(new ImagesAsAltText());
        builder.Extensions.Add(new MermaidImageExtension());
        return MarkdownSafety.Build(builder);
    }
}
