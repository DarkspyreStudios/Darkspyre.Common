using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Extensions.MediaLinks;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Darkspyre.Common.Markdown;

/// <summary>The one Markdown safety policy for every Darkspyre surface.</summary>
/// <remarks>
/// The allow-list is absolute http and https addresses with a host, mailto addresses, and relative paths. Without a link
/// source, a refused link or image target is emptied, so it cannot be followed or loaded, and a refused autolink keeps no
/// address. With a source, every target that is not an http, https or mailto address is a link only when the source
/// accepts it, a refused link shows as its own text, a refused image is emptied and a refused autolink becomes literal
/// text. Generic attributes and media links are off, so Markdown cannot add attributes or embed players, and raw HTML is
/// parsed as text.
/// </remarks>
public static class MarkdownSafety
{
    /// <summary>Whether a link to <paramref name="target"/> may be activated: an absolute http or https address with a
    /// host, a mailto address, or a relative path.</summary>
    public static bool IsAllowedTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target != target.Trim() || target.StartsWith("//", StringComparison.Ordinal)
            || target.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '\\'))
            return false;
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute))
            return absolute.Scheme is "http" or "https" or "mailto" && (absolute.Scheme == "mailto" || !string.IsNullOrEmpty(absolute.Host));
        return Uri.TryCreate(target, UriKind.Relative, out _);
    }

    /// <summary>The advanced preset without GenericAttributesExtension and MediaLinkExtension, plus the link check for
    /// <paramref name="source"/>. The caller adds its own extensions, then calls <see cref="Build"/>.</summary>
    /// <param name="source">Decides which targets that are not web or mail addresses are links; null allows only the
    /// allow-list.</param>
    public static MarkdownPipelineBuilder CreateBuilder(IMarkdownLinkSource? source)
    {
        MarkdownPipelineBuilder builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // The advanced preset includes arbitrary attributes and unsandboxed media embeds.
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        builder.Extensions.TryRemove<MediaLinkExtension>();
        builder.Extensions.Add(new InertLinks(source));
        return builder;
    }

    /// <summary>Applies DisableHtml and builds the pipeline.</summary>
    public static MarkdownPipeline Build(MarkdownPipelineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.DisableHtml().Build();
    }

    // Without a source, the allow-list decides. With one, a web or mail target is still checked by the allow-list, and
    // any other target is a link only when the source accepts it.
    internal static bool Allows(IMarkdownLinkSource? source, string? target)
    {
        if (source is null) return IsAllowedTarget(target);
        if (string.IsNullOrEmpty(target)) return false;
        return IsWebTarget(target) ? IsAllowedTarget(target) : source.Accepts(target);
    }

    internal static bool IsWebTarget(string target) => target.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks every link and image the Markdown wrote. Without a source, a refused target is emptied, so it cannot be
    /// followed or loaded. With a source, a refused link is replaced by its own text and a refused image is emptied.
    /// </summary>
    private sealed class InertLinks(IMarkdownLinkSource? source) : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += this.Guard;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }

        private void Guard(MarkdownDocument document)
        {
            foreach (LinkInline link in document.Descendants<LinkInline>().ToList())
            {
                string? target = link.GetDynamicUrl?.Invoke() ?? link.Url;
                link.GetDynamicUrl = null;
                if (Allows(source, target)) link.Url = target;
                else if (source is null || link.IsImage) link.Url = string.Empty;
                else Unwrap(link);
            }
            foreach (AutolinkInline link in document.Descendants<AutolinkInline>().ToList())
            {
                if (Allows(source, link.IsEmail ? "mailto:" + link.Url : link.Url)) continue;
                if (source is not null)
                {
                    link.ReplaceBy(new LiteralInline(link.Url) { Span = link.Span });
                    continue;
                }
                link.Url = string.Empty;
                link.IsEmail = false;
            }
        }

        // The link's children take its place, so its text shows as ordinary text.
        private static void Unwrap(LinkInline link)
        {
            Inline? child = link.FirstChild;
            while (child is not null)
            {
                Inline? next = child.NextSibling;
                child.Remove();
                link.InsertBefore(child);
                child = next;
            }
            link.Remove();
        }
    }
}
