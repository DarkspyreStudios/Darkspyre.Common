using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>One tool result's content parts for one audience, with copy and save actions, retained previews, omissions and structured details.</summary>
public static class ToolOutput
{
    private static readonly string[] InlineImages = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>Draws the person's or inspection audience of a result, with retained previews.</summary>
    public static StackPanel Of(TranscriptOutput output, bool direct, IReadOnlyList<TranscriptPreview> previews, TranscriptContext context)
    {
        StackPanel view = ConversationText.In(new StackPanel { Spacing = 8 });
        view.Classes.Add("tool-output");
        foreach (TranscriptPart part in output.Parts)
        {
            if (part.ForPerson != direct) continue;
            long? omitted = part.OmittedBytes;
            TranscriptPreview? preview = previews.FirstOrDefault(item => item.Position == part.Position);
            StackPanel shown = new() { Spacing = 4 };
            shown.Classes.Add("tool-output-part");
            switch (part)
            {
                case TranscriptTextPart text:
                    shown.Children.Add(Copy("Copy text", "Copy tool text", text.Text));
                    shown.Children.Add(direct ? ConversationText.In(new ConversationMarkdown(context, text.Text) { Classes = { "body" } }) : Raw(text.Text));
                    break;
                case TranscriptResourcePart resource:
                    shown.Children.Add(Forms.Text($"{resource.Label}: {resource.Uri}"));
                    if (preview is not null) shown.Children.Add(Preview(preview, context, AttachmentPreviewSize.Thumbnail));
                    shown.Children.Add(Copy("Copy path", "Copy resource path", resource.Uri));
                    if (resource.BodyText is { } body)
                    {
                        shown.Children.Add(Copy("Copy text", "Copy resource text", body));
                        shown.Children.Add(Raw(body));
                    }
                    else if (resource.BodyBytes is { } bytes && omitted is null)
                        shown.Children.Add(Save($"Save resource ({bytes.Length} bytes)", resource.SuggestedName, bytes, context));
                    break;
                case TranscriptBinaryPart binary:
                    shown.Children.Add(Forms.Text($"{binary.Label} · {binary.MediaType}"));
                    if (preview is not null)
                        shown.Children.Add(Preview(preview, context, direct ? AttachmentPreviewSize.Display : AttachmentPreviewSize.Thumbnail));
                    if (omitted is null && binary.Bytes is { } data)
                    {
                        if (preview is null && direct && InlineImages.Contains(binary.MediaType) && Decode(data) is { } bitmap)
                        {
                            Image image = new() { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform, StretchDirection = Avalonia.Media.StretchDirection.DownOnly };
                            image.Classes.Add("tool-output-image");
                            Avalonia.Automation.AutomationProperties.SetName(image, binary.Label);
                            shown.Children.Add(image);
                        }
                        shown.Children.Add(Save($"Save content ({data.Length} bytes)", binary.SuggestedName, data, context));
                    }
                    break;
            }
            if (omitted is { } length) shown.Children.Add(Forms.Text($"{length} bytes were not retained in session history.", "tool-output-omission"));
            if (part.Metadata is { } metadata) shown.Children.Add(Raw(JsonText(metadata)));
            view.Children.Add(shown);
        }
        if (!direct || output.Parts.All(part => part.ForPerson))
        {
            if (output.Structured is { } structured)
            {
                view.Children.Add(Forms.Text("Structured result"));
                view.Children.Add(Raw(JsonText(structured)));
            }
            if (output.Details is { } details)
            {
                view.Children.Add(Forms.Text("Details"));
                view.Children.Add(Raw(JsonText(details)));
            }
            if (output.Parts.Count == 0) view.Children.Add(Forms.Text("No content returned."));
        }
        return view;
    }

    /// <summary>Shows structured content and omitted or inspection content behind a disclosure.</summary>
    public static Expander Inspection(TranscriptOutput tool, IReadOnlyList<TranscriptPreview> previews, TranscriptContext context)
    {
        Expander inspection = new() { Header = "Inspect retained content", HorizontalAlignment = HorizontalAlignment.Stretch };
        inspection.Classes.Add("tool-rich-inspection");
        inspection.Expanding += (_, _) => inspection.Content ??= Of(tool, false, previews, context);
        return inspection;
    }

    private static AttachmentPreviewView Preview(TranscriptPreview preview, TranscriptContext context, AttachmentPreviewSize size) =>
        new(preview, size, context.Images, showName: false);

    private static TextBlock Raw(string text) => Forms.Text(text, "tool-output-text");

    // A copy button that shows the check for two seconds once its text is on the clipboard.
    private static Button Copy(string label, string name, string text)
    {
        Button button = null!;
        button = Action(label, name, () => CopyFeedback.CopyAsync(button, text));
        return button;
    }

    private static Button Action(string label, string name, Func<Task> click)
    {
        Button button = Forms.Button(label, "ghost", click, ButtonSize.Small);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    private static Button Save(string label, string suggestedName, ReadOnlyMemory<byte> data, TranscriptContext context) =>
        Action(label, label, () => context.Save(suggestedName, data));

    private static Bitmap? Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            using MemoryStream stream = new(data.ToArray(), writable: false);
            return new Bitmap(stream);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static string JsonText(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "Undefined JSON" : value.GetRawText();
}
