using Darkspyre.Common.Markdown;

namespace Darkspyre.Mobile.Components;

/// <summary>A link source that accepts and finds no target, so only http, https and mailto targets stay links.</summary>
internal sealed class NoLinkSource : IMarkdownLinkSource
{
    public static readonly NoLinkSource Instance = new();

    private NoLinkSource()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<MarkdownLinkSpan> Find(string text) => [];

    public bool Accepts(string target) => false;
}
