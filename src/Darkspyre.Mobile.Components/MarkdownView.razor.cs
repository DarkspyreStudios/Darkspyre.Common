using Darkspyre.Common.Markdown;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Darkspyre.Mobile.Components;

/// <summary>
/// Renders Markdown as HTML under the shared Markdown safety policy, and colours code blocks in a language
/// highlight.js knows.
/// </summary>
/// <remarks>
/// Only http, https and mailto targets stay links; every other link or autolink target, relative ones included, shows as
/// its own text. Every image shows as its alt text, so no remote image loads. Raw HTML shows as text, and no attribute,
/// style or embed reaches the page. After each render the page's <c>highlightNew</c> function from <c>markdown.js</c>
/// colours the code blocks not yet coloured; a block in an unknown language stays plain text. The host page loads
/// <c>_content/Darkspyre.Mobile.Components/highlight/highlight.min.js</c> and
/// <c>_content/Darkspyre.Mobile.Components/markdown.js</c>.
/// </remarks>
/// <gotcha>A tapped link navigates the web view unless the host keeps it on the app's own pages; a MAUI host calls
/// <c>UseDarkspyreLinkPolicy</c> from Darkspyre.Mobile.Maui.</gotcha>
public sealed partial class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();

    private ElementReference element;
    private string html = string.Empty;
    private string? parsed;

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
        this.html = Markdig.Markdown.ToHtml(text, Pipeline);
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
    }

    private static MarkdownPipeline CreatePipeline()
    {
        MarkdownPipelineBuilder builder = MarkdownSafety.CreateBuilder(NoLinkSource.Instance);
        builder.Extensions.Add(new ImagesAsAltText());
        return MarkdownSafety.Build(builder);
    }
}
