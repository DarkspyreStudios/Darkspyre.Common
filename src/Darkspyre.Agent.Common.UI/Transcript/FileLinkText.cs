using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Darkspyre.Common.Markdown;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>
/// A tool line's text with the files it names as links: each span the finder finds is drawn as a <c>file-link</c> run,
/// and a press on one activates its target and is handled, so a press on a link in an expander's header does not toggle
/// the expander. <see cref="TextBlock.Text"/> always holds the whole value. When the finder's files change, the value is
/// searched again on a worker thread, and the links are drawn again only when they differ.
/// </summary>
internal sealed class FileLinkText : TextBlock
{
    private readonly IMarkdownLinkSource? finder;
    private readonly Func<string, Control, Task> activate;
    private IReadOnlyList<MarkdownLinkSpan> links = [];
    private string value = string.Empty;
    private bool underline;
    // Counts the searches a finder change started; only the latest one draws. Used only on the UI thread.
    private int relinks;

    /// <summary>Creates the text; <paramref name="finder"/> null shows it without links.</summary>
    internal FileLinkText(IMarkdownLinkSource? finder, Func<string, Control, Task> activate)
    {
        this.finder = finder;
        this.activate = activate;
        // Without a background only the drawn glyphs take a press; the whole line must, so a link's press reaches it.
        this.Background = Brushes.Transparent;
        // The theme says whether links are underlined; a change to its resources draws the links again.
        this.ResourcesChanged += (_, _) =>
        {
            if (this.LinksUnderlined() != this.underline) this.Render();
        };
    }

    /// <summary>The text shown.</summary>
    internal string Value
    {
        get => this.value;
        set
        {
            string next = value ?? string.Empty;
            if (next == this.value && this.Text == next) return;
            this.value = next;
            this.Render();
        }
    }

    /// <summary>The link spans drawn now.</summary>
    internal IReadOnlyList<MarkdownLinkSpan> Links => this.links;

    /// <summary>Styled as TextBlock, so the tool line's TextBlock styles still apply (ConversationStyles.axaml:140).</summary>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.finder is not null) this.finder.Changed += this.OnFinderChanged;
        this.Render();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (this.finder is not null) this.finder.Changed -= this.OnFinderChanged;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || this.LinkAt(e.GetPosition(this)) is not { } link) return;
        e.Handled = true;
        _ = this.activate(link.Target, this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        bool over = this.LinkAt(e.GetPosition(this)) is not null;
        if (over != (this.Cursor is not null)) this.Cursor = over ? new Cursor(StandardCursorType.Hand) : null;
    }

    private void OnFinderChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = this.RelinkAsync());

    // A finder change can come once for every file a long transcript names, so the search runs off the UI thread and the
    // UI thread draws only links that changed, for the value still shown.
    private async Task RelinkAsync()
    {
        if (this.finder is not { } source) return;
        int relink = ++this.relinks;
        string searched = this.value;
        IReadOnlyList<MarkdownLinkSpan> found;
        try { found = await Task.Run(() => source.Find(searched)); }
        catch (Exception error) when (error is not OutOfMemoryException) { return; }
        if (relink != this.relinks || searched != this.value || found.SequenceEqual(this.links)) return;
        this.Draw(found);
    }

    // The link under a point of this control, or null.
    private MarkdownLinkSpan? LinkAt(Point point)
    {
        if (this.links.Count == 0) return null;
        TextHitTestResult hit = this.TextLayout.HitTestPoint(point - new Point(this.Padding.Left, this.Padding.Top));
        if (!hit.IsInside) return null;
        int position = hit.TextPosition;
        foreach (MarkdownLinkSpan link in this.links)
            if (position >= link.Start && position < link.Start + link.Length) return link;
        return null;
    }

    // The theme's link-underline resource.
    private bool LinksUnderlined() => this.TryFindResource("link-underline", this.ActualThemeVariant, out object? found) && found is true;

    private void Render() => this.Draw(this.finder?.Find(this.value) ?? []);

    private void Draw(IReadOnlyList<MarkdownLinkSpan> found)
    {
        this.underline = this.LinksUnderlined();
        this.links = found;
        this.Text = this.value;
        if (this.links.Count == 0)
        {
            this.Inlines?.Clear();
            return;
        }
        InlineCollection inlines = [];
        int position = 0;
        foreach (MarkdownLinkSpan link in this.links)
        {
            if (link.Start > position) inlines.Add(new Run(this.value[position..link.Start]));
            Run run = new(this.value.Substring(link.Start, link.Length));
            run.Classes.Add("file-link");
            if (this.underline) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            inlines.Add(run);
            position = link.Start + link.Length;
        }
        if (position < this.value.Length) inlines.Add(new Run(this.value[position..]));
        this.Inlines = inlines;
    }
}
