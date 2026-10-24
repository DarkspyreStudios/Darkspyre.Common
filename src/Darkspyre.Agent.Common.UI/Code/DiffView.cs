using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>
/// A read-only unified diff of two texts: a header per hunk on a darker band, then each line with one line number (the
/// old number for a removed line, the new number for the others) and a plus or minus mark; removed lines on the theme's
/// danger colour sit above the added lines that replace them, on its ok colour. A hunk header names the section it is in
/// after the line ranges: the nearest line above the hunk that starts with a letter, an underscore or a dollar sign, or a
/// Markdown heading without its hashes. A last line with no line break is followed by a "No final newline" note. Line
/// breaks take part in the comparison.
/// </summary>
/// <gotcha>A null text compares as an empty file. The view names neither version; its caller shows which versions it
/// compares and names an absent one, such as "Not in the last commit".</gotcha>
public sealed class DiffView : TemplatedControl
{
    /// <summary>The old text, or null for none.</summary>
    public static readonly StyledProperty<string?> OldTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(OldText));

    /// <summary>The new text, or null for none.</summary>
    public static readonly StyledProperty<string?> NewTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(NewText));

    private readonly Decorator root = new();
    private readonly TextBlock status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly Unified unified;
    private CancellationTokenSource? computing;

    /// <summary>Creates the view.</summary>
    public DiffView()
    {
        this.status.Classes.Add("diff-status");
        this.unified = new Unified(this);
        this.root.Child = this.unified.View;
        Panel content = new();
        content.Children.Add(this.root);
        content.Children.Add(this.status);
        this.Template = new FuncControlTemplate<DiffView>((_, _) => content);
        this.Show([], null);
    }

    /// <summary>The old text, or null for none.</summary>
    public string? OldText
    {
        get => this.GetValue(OldTextProperty);
        set => this.SetValue(OldTextProperty, value);
    }

    /// <summary>The new text, or null for none.</summary>
    public string? NewText
    {
        get => this.GetValue(NewTextProperty);
        set => this.SetValue(NewTextProperty, value);
    }

    /// <summary>Marks every case-insensitive match of <paramref name="query"/> in the shown diff, selects the first, and returns how many there are.</summary>
    public int Find(string query) => this.unified.Finder.Find(query);

    /// <summary>Selects the match after the current one, wrapping to the first.</summary>
    public void FindNext() => this.unified.Finder.FindNext();

    /// <summary>Selects the match before the current one, wrapping to the last.</summary>
    public void FindPrevious() => this.unified.Finder.FindPrevious();

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.unified.Finder.CurrentMatch;

    /// <summary>
    /// Shows <paramref name="hunks"/> computed elsewhere, such as on another machine, instead of comparing
    /// <see cref="OldText"/> and <see cref="NewText"/>. A comparison still running is cancelled. The headers name no
    /// section, because the lines above a hunk are not part of it.
    /// </summary>
    /// <gotcha>Setting <see cref="OldText"/> or <see cref="NewText"/> afterwards compares those texts again and replaces
    /// these hunks.</gotcha>
    public void ShowHunks(IReadOnlyList<DiffHunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        this.computing?.Cancel();
        this.computing = null;
        this.Show([.. hunks.Select(hunk => (hunk, (string?)null))], null);
    }

    /// <summary>The hunks shown, after the latest comparison finished.</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; private set; } = [];

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OldTextProperty || change.Property == NewTextProperty) this.Compare();
    }

    // The comparison runs off the UI thread; a newer pair of texts cancels it and only the latest result is shown.
    private void Compare()
    {
        this.computing?.Cancel();
        CancellationTokenSource run = new();
        this.computing = run;
        IReadOnlyList<TextLine> before = TextLine.Split(this.OldText), after = TextLine.Split(this.NewText);
        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiffHunk> hunks = TextDiff.Compute(before, after, run.Token);
                return (Hunks: hunks.Select(hunk => (hunk, Section(before, hunk))).ToArray(), Error: (string?)null);
            }
            catch (IOException error) { return (Hunks: Array.Empty<(DiffHunk, string?)>(), Error: error.Message); }
        }, run.Token).ContinueWith(done =>
        {
            if (done.IsCanceled || run.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(this.computing, run)) this.Show(done.Result.Hunks, done.Result.Error);
            });
        }, TaskScheduler.Default);
    }

    private void Show(IReadOnlyList<(DiffHunk Hunk, string? Section)> hunks, string? failure)
    {
        this.Hunks = [.. hunks.Select(shown => shown.Hunk)];
        bool any = this.unified.Show(hunks);
        this.root.IsVisible = any;
        this.status.Text = failure ?? "No text differences.";
        this.status.IsVisible = !any;
    }

    // The section a hunk is in, as git names it: the nearest old line above the hunk that starts with a letter, an
    // underscore or a dollar sign; a Markdown heading counts too and is named without its hashes.
    private static string? Section(IReadOnlyList<TextLine> old, DiffHunk hunk)
    {
        for (int index = Math.Min(hunk.OldStartLine - 1, old.Count) - 1; index >= 0; index--)
        {
            string text = old[index].Text;
            if (text.Length == 0) continue;
            if (text[0] == '#')
            {
                string heading = text.TrimStart('#');
                if (heading.Length > 0 && heading.Length < text.Length && text.Length - heading.Length <= 6 && char.IsWhiteSpace(heading[0]))
                    return heading.Trim();
                continue;
            }
            if (char.IsLetter(text[0]) || text[0] is '_' or '$') return text.TrimEnd();
        }
        return null;
    }

    private enum DiffRowKind { Header, Context, Add, Remove, NoNewline }

    private sealed record DiffRow(DiffRowKind Kind, int? Line);

    private static string HunkHeader(DiffHunk hunk, string? section) => string.Create(CultureInfo.InvariantCulture,
        $"@@ -{hunk.OldStartLine},{hunk.OldCount} +{hunk.NewStartLine},{hunk.NewCount} @@{(section is null ? string.Empty : " " + section)}");

    /// <summary>The one column: the unified rows with their line number and mark.</summary>
    private sealed class Unified
    {
        private readonly TextEditor editor = CodeEditors.Create(lineNumbers: false);
        private readonly DiffGutter gutter;
        private List<DiffRow> rows = [];

        public Unified(DiffView owner)
        {
            this.gutter = new DiffGutter(owner, () => this.rows);
            this.editor.TextArea.LeftMargins.Insert(0, this.gutter);
            this.editor.TextArea.TextView.Margin = new Thickness(0);
            // A diff ends at its last row: a short one shows no scroll bar, as the Git panel mockup draws it.
            this.editor.Options.AllowScrollBelowDocument = false;
            Func<int, DiffRowKind?> kindOf = index => index < this.rows.Count ? this.rows[index].Kind : null;
            this.editor.TextArea.TextView.BackgroundRenderers.Add(new RowShading(owner, kindOf));
            this.editor.TextArea.TextView.LineTransformers.Add(new NoteColour(owner, kindOf));
            this.Finder = new EditorFind(this.editor, owner);
            global::Avalonia.Automation.AutomationProperties.SetName(this.editor, "Read-only file comparison");
            this.View = this.editor;
        }

        public Control View { get; }

        public EditorFind Finder { get; }

        public bool Show(IReadOnlyList<(DiffHunk Hunk, string? Section)> hunks)
        {
            List<DiffRow> shown = [];
            StringBuilder text = new();
            void Line(DiffRow row, string content)
            {
                if (shown.Count > 0) text.Append('\n');
                text.Append(content);
                shown.Add(row);
            }
            foreach ((DiffHunk hunk, string? section) in hunks)
            {
                Line(new(DiffRowKind.Header, null), HunkHeader(hunk, section));
                foreach (DiffLine line in hunk.Lines)
                {
                    DiffRowKind kind = line.Kind switch { DiffLineKind.Add => DiffRowKind.Add, DiffLineKind.Remove => DiffRowKind.Remove, _ => DiffRowKind.Context };
                    Line(new(kind, kind == DiffRowKind.Remove ? line.OldLine : line.NewLine), line.Text);
                    if (line.Ending == LineEnding.None) Line(new(DiffRowKind.NoNewline, null), "No final newline");
                }
            }
            this.rows = shown;
            this.Finder.Clear();
            this.editor.Document.Text = text.ToString();
            this.gutter.InvalidateMeasure();
            this.editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return shown.Count > 0;
        }
    }

    /// <summary>
    /// The line number, right-aligned in a column at least 34 px wide, and the 14 px mark column beside each row: a minus
    /// on the theme's danger colour for a removed line, a plus on its ok colour for an added one. A hunk header's band
    /// runs under the gutter too.
    /// </summary>
    private sealed class DiffGutter(DiffView owner, Func<IReadOnlyList<DiffRow>> rows) : AbstractMargin
    {
        private const double NumberWidth = 34;
        private const double NumberGap = 6;
        private const double MarkWidth = 14;

        protected override Size MeasureOverride(Size availableSize) => new(this.NumberColumn() + MarkWidth, 0);

        public override void Render(DrawingContext context)
        {
            TextView? view = this.TextView;
            if (view is null || !view.VisualLinesValid) return;
            IReadOnlyList<DiffRow> shown = rows();
            IBrush muted = CodeEditors.Brush(owner, "muted.brush", Brushes.Gray);
            IBrush ok = CodeEditors.Brush(owner, "ok.brush", Brushes.Green);
            IBrush danger = CodeEditors.Brush(owner, "danger.brush", Brushes.Red);
            double numbers = this.NumberColumn();
            foreach (VisualLine line in view.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= shown.Count) continue;
                DiffRow row = shown[index];
                double top = line.VisualTop - view.VerticalOffset;
                if (RowShading.BrushFor(owner, row.Kind) is { } band) context.FillRectangle(band, new Rect(0, top, this.Bounds.Width, line.Height));
                double y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                if (row.Line is { } number)
                {
                    FormattedText text = this.Text(number.ToString(CultureInfo.InvariantCulture), muted);
                    context.DrawText(text, new Point(numbers - NumberGap - text.Width, y));
                }
                if (row.Kind is DiffRowKind.Add or DiffRowKind.Remove)
                    context.DrawText(this.Text(row.Kind == DiffRowKind.Add ? "+" : "−", row.Kind == DiffRowKind.Add ? ok : danger), new Point(numbers, y));
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= this.Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += this.Redraw;
        }

        // Wide enough for the largest line number shown and its gap, and never under 34 px.
        private double NumberColumn()
        {
            int widest = rows().Select(row => row.Line ?? 0).DefaultIfEmpty(0).Max();
            double digits = this.Text(widest.ToString(CultureInfo.InvariantCulture), Brushes.Black).Width;
            return Math.Max(NumberWidth, digits + (NumberGap * 2));
        }

        private FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(this.TextView?.GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default),
            this.TextView?.GetValue(TextBlock.FontSizeProperty)
                ?? (this.TryFindResource("font-size-code", out object? size) && size is double code ? code : this.GetValue(TextBlock.FontSizeProperty)), brush);

        private void Redraw(object? sender, EventArgs args) => this.InvalidateVisual();
    }

    /// <summary>Shades added and removed rows and the hunk headers across the full width.</summary>
    private sealed class RowShading(DiffView owner, Func<int, DiffRowKind?> kindOf) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        // Added rows on the ok colour, removed rows on the danger colour, hunk headers on the darker background colour.
        public static IBrush? BrushFor(Control owner, DiffRowKind kind) => kind switch
        {
            DiffRowKind.Add => CodeEditors.Brush(owner, "ok.brush.15", new SolidColorBrush(Colors.Green, 0.15)),
            DiffRowKind.Remove => CodeEditors.Brush(owner, "danger.brush.15", new SolidColorBrush(Colors.Red, 0.15)),
            DiffRowKind.Header => CodeEditors.Brush(owner, "bg.brush", new SolidColorBrush(Colors.Gray, 0.15)),
            _ => null,
        };

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            foreach (VisualLine line in textView.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || kindOf(index) is not { } kind || BrushFor(owner, kind) is not { } brush) continue;
                double top = line.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(brush, new Rect(0, top, Math.Max(textView.Bounds.Width, ((IScrollable)textView).Extent.Width), line.Height));
            }
        }
    }

    /// <summary>Draws the hunk headers and the "No final newline" notes in the theme's muted colour.</summary>
    private sealed class NoteColour(DiffView owner, Func<int, DiffRowKind?> kindOf) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (kindOf(line.LineNumber - 1) is not (DiffRowKind.Header or DiffRowKind.NoNewline) || line.Length == 0) return;
            IBrush muted = CodeEditors.Brush(owner, "muted.brush", Brushes.Gray);
            this.ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(muted));
        }
    }
}
