using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;

namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>Find in a read-only editor: every case-insensitive match marked, the selected one stronger, wrapping in both directions.</summary>
internal sealed class EditorFind
{
    private readonly TextEditor editor;
    private readonly Control owner;
    private readonly List<(int Offset, int Length)> matches = [];
    private int current = -1;

    public EditorFind(TextEditor editor, Control owner)
    {
        this.editor = editor;
        this.owner = owner;
        editor.TextArea.TextView.BackgroundRenderers.Add(new RangeMarks(this.Marks));
    }

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.current + 1;

    /// <summary>Forgets the matches, as when the text changes.</summary>
    public void Clear()
    {
        this.matches.Clear();
        this.current = -1;
    }

    public int Find(string query)
    {
        this.Clear();
        string text = this.editor.Document.Text;
        if (!string.IsNullOrEmpty(query))
        {
            for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                at = text.IndexOf(query, at + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase))
                this.matches.Add((at, query.Length));
        }
        if (this.matches.Count > 0) this.Select(0);
        this.editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        return this.matches.Count;
    }

    public void FindNext()
    {
        if (this.matches.Count > 0) this.Select((this.current + 1) % this.matches.Count);
    }

    public void FindPrevious()
    {
        if (this.matches.Count > 0) this.Select((this.current - 1 + this.matches.Count) % this.matches.Count);
    }

    private void Select(int index)
    {
        this.current = index;
        (int offset, int length) = this.matches[index];
        this.editor.Select(offset, length);
        AvaloniaEdit.Document.TextLocation location = this.editor.Document.GetLocation(offset);
        this.editor.ScrollTo(location.Line, location.Column);
        this.editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    private IReadOnlyList<(int Offset, int Length, IBrush Brush)> Marks()
    {
        if (this.matches.Count == 0) return [];
        IBrush match = CodeEditors.Brush(this.owner, "accent.brush.20", new SolidColorBrush(Colors.Gold, 0.35));
        IBrush selected = CodeEditors.Brush(this.owner, "accent.brush.50", new SolidColorBrush(Colors.Orange, 0.6));
        return [.. this.matches.Select((item, index) => (item.Offset, item.Length, index == this.current ? selected : match))];
    }
}
