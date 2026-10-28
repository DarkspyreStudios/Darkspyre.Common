using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Icons;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>
/// One attachment as the draft, queue and transcript show it: a ready image, a document's type icon, or an explicit
/// unavailable state. A ready image is read lazily through <see cref="AttachmentPreviewImages"/>, which holds it while
/// this view is in a window. The image is shown at its stored size or smaller, never larger.
/// </summary>
public sealed class AttachmentPreviewView : UserControl
{
    private readonly string name;
    private readonly PreviewState state;
    private readonly IconSource? documentIcon;
    private readonly string documentLabel;
    private readonly object? image;
    private readonly AttachmentPreviewSize size;
    private readonly AttachmentPreviewImages? images;
    private readonly ContentControl body = new();
    private AttachmentPreviewImages.Lease? lease;

    /// <summary>Creates the view of the supplied attachment preview.</summary>
    /// <param name="preview">The owner's neutral presentation and image key.</param>
    /// <param name="size">A small thumbnail, or the larger transcript image.</param>
    /// <param name="images">The images the view leases its image from; null shows no image.</param>
    /// <param name="showName">Whether the name is shown under the preview.</param>
    public AttachmentPreviewView(TranscriptPreview preview, AttachmentPreviewSize size, AttachmentPreviewImages? images, bool showName = true)
    {
        string name = preview.Name;
        this.name = name;
        this.state = preview.State;
        this.documentIcon = preview.DocumentIcon;
        this.documentLabel = preview.DocumentLabel;
        this.image = preview.State == PreviewState.ReadyImage ? preview.ImageKey : null;
        this.size = size;
        this.images = images;
        StackPanel figure = new() { Spacing = 4 };
        figure.Classes.Add("attachment-preview");
        figure.Classes.Add(size == AttachmentPreviewSize.Display ? "attachment-preview-display" : "attachment-preview-thumbnail");
        figure.Children.Add(this.body);
        if (showName)
        {
            TextBlock caption = new() { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
            caption.Classes.Add("attachment-preview-name");
            ToolTip.SetTip(caption, name);
            figure.Children.Add(caption);
        }
        this.Content = figure;
        this.ShowIcon(this.image is not null && images is not null && preview.State == PreviewState.ReadyImage);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.image is null || this.images is null || this.lease is not null) return;
        AttachmentPreviewImages.Lease current = this.lease = this.images.Acquire(this.image);
        // A decoded image shows at once, with no loading icon, so a redrawn row keeps its height.
        if (current.Image.IsCompletedSuccessfully) this.ShowImage(current.Image.Result);
        else _ = this.ShowAsync(current);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        this.lease?.Dispose();
        this.lease = null;
        this.ShowIcon(this.image is not null && this.images is not null);
    }

    private async Task ShowAsync(AttachmentPreviewImages.Lease current)
    {
        PreviewImage? made = await current.Image;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(current, this.lease)) this.ShowImage(made);
        });
    }

    private void ShowImage(PreviewImage? made)
    {
        if (made is null)
        {
            this.ShowIcon(false);
            return;
        }
        Image shown = new() { Source = made.Bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        if (made.Width is { } width) shown.MaxWidth = width;
        if (made.Height is { } height) shown.MaxHeight = height;
        shown.Classes.Add("attachment-preview-image");
        Avalonia.Automation.AutomationProperties.SetName(shown, $"Preview of {this.name}");
        this.body.Content = shown;
    }

    // Loading shows the image icon; a document its type icon; anything else the unavailable mark.
    private void ShowIcon(bool loading)
    {
        (IconSource icon, string label, string? kind) = loading
            ? (AttachmentPreviewIcons.Image, $"Loading the preview of {this.name}", "attachment-preview-loading")
            : this.state == PreviewState.Document
                ? (this.documentIcon ?? AttachmentPreviewIcons.Other, this.documentLabel, null)
                : (AttachmentPreviewIcons.UnavailableImage, "Image preview unavailable", "attachment-preview-unavailable");
        Border mark = new() { Child = new Icon { Source = icon }, HorizontalAlignment = HorizontalAlignment.Left };
        mark.Classes.Add("attachment-preview-icon");
        if (kind is not null) mark.Classes.Add(kind);
        Avalonia.Automation.AutomationProperties.SetName(mark, label);
        if (!loading && this.state != PreviewState.Document && this.size == AttachmentPreviewSize.Display)
        {
            StackPanel unavailable = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            unavailable.Children.Add(mark);
            unavailable.Children.Add(Forms.Text("Preview unavailable", "attachment-preview-note"));
            this.body.Content = unavailable;
        }
        else this.body.Content = mark;
    }
}

/// <summary>The retained previews a queued prompt or a transcript turn holds, in source position order.</summary>
public static class AttachmentPreviewList
{
    /// <summary>
    /// The supplied previews in source order, or null when there are none.
    /// </summary>
    public static WrapPanel? Of(IReadOnlyList<TranscriptPreview> previews,
        AttachmentPreviewImages? images, AttachmentPreviewSize size = AttachmentPreviewSize.Display)
    {
        if (previews.Count == 0) return null;
        WrapPanel list = new() { ItemSpacing = 8, LineSpacing = 8 };
        list.Classes.Add("attachment-previews");
        if (size == AttachmentPreviewSize.Display) list.Classes.Add("attachment-previews-display");
        foreach (TranscriptPreview preview in previews)
            list.Children.Add(new AttachmentPreviewView(preview, size, images));
        return list;
    }
}
