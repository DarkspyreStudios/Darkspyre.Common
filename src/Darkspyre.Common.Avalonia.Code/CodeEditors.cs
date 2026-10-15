using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Darkspyre.Common.Avalonia.Code;

/// <summary>The read-only editor both viewers draw text with, themed through the theme token resources.</summary>
internal static class CodeEditors
{
    /// <summary>A read-only editor with the code font, the theme's text and panel colours, and no word wrap.</summary>
    public static TextEditor Create(bool lineNumbers)
    {
        TextEditor editor = new()
        {
            IsReadOnly = true,
            ShowLineNumbers = lineNumbers,
            WordWrap = false,
            Document = new TextDocument(),
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"),
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        editor.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
        Bind(editor, TemplatedControl.FontFamilyProperty, "font-code");
        Bind(editor, TemplatedControl.ForegroundProperty, "text.brush");
        Bind(editor, TemplatedControl.BackgroundProperty, "panel.brush");
        Bind(editor, TextEditor.LineNumbersForegroundProperty, "muted.brush");
        return editor;
    }

    /// <summary>Follows the resource <paramref name="key"/> for <paramref name="property"/> while one is supplied.</summary>
    public static void Bind(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key, value => value ?? AvaloniaProperty.UnsetValue));

    /// <summary>The brush the resource <paramref name="key"/> names under <paramref name="control"/>, or <paramref name="fallback"/>.</summary>
    public static IBrush Brush(Control control, string key, IBrush fallback) =>
        control.TryFindResource(key, control.ActualThemeVariant, out object? found) && found is IBrush brush ? brush : fallback;
}

/// <summary>Draws a translucent box behind each text range a viewer marks, such as find matches.</summary>
internal sealed class RangeMarks(Func<IReadOnlyList<(int Offset, int Length, IBrush Brush)>> ranges) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        foreach ((int offset, int length, IBrush brush) in ranges())
        {
            BackgroundGeometryBuilder builder = new() { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = Math.Max(1, length) });
            if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(brush, null, geometry);
        }
    }
}
