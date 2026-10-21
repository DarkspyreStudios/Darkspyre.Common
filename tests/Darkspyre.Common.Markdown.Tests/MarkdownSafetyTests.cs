using Darkspyre.Common.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace Darkspyre.Common.Markdown.Tests;

public sealed class MarkdownSafetyTests
{
    [Fact]
    public void PipelineKeepsOnlyWebAndMailLinksAndDropsAttributes()
    {
        MarkdownPipeline pipeline = MarkdownSafety.Build(MarkdownSafety.CreateBuilder(null));
        string text = "[bad](javascript:alert(1)) [odd](//host/x) [good](https://example.com/a) [local](docs/readme.md)\n\n"
            + "<javascript:alert(1)> <https://example.com/b>\n\n[x](https://a){onclick=x}";
        MarkdownDocument document = Markdig.Markdown.Parse(text, pipeline);

        string[] links = [.. document.Descendants<LinkInline>().Select(link => link.Url ?? "(null)")];
        string[] autolinks = [.. document.Descendants<AutolinkInline>().Select(link => link.Url)];
        string html = document.ToHtml(pipeline);

        Assert.Equal([string.Empty, string.Empty, "https://example.com/a", "docs/readme.md", "https://a"], links);
        Assert.Equal([string.Empty, "https://example.com/b"], autolinks);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=\"", html, StringComparison.Ordinal);
        Assert.Contains("{onclick=x}", html, StringComparison.Ordinal);
    }
}
