using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Darkspyre.Common.Avalonia.Code;

/// <summary>
/// A read-only unified diff of two texts: a header per hunk, then each line with its old and new line numbers and a
/// plus or minus mark, added lines on the theme's ok colour and removed lines on its danger colour. A last line with
/// no line break is followed by a "No final newline" note. Line breaks take part in the comparison.
/// </summary>
public sealed class DiffView : TemplatedControl
{
    /// <summary>The old text, or null for none.</summary>
    public static readonly StyledProperty<string?> OldTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(OldText));

    /// <summary>The new text, or null for none.</summary>
    public static readonly StyledProperty<string?> NewTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(NewText));

    private readonly TextEditor editor = CodeEditors.Create(lineNumbers: false);
    private readonly TextBlock status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly DiffGutter gutter;
    private readonly EditorFind finder;
    private IReadOnlyList<DiffRow> rows = [];
    private CancellationTokenSource? computing;

    /// <summary>Creates the view.</summary>
    public DiffView()
    {
        this.gutter = new DiffGutter(() => this.rows);
        this.editor.TextArea.LeftMargins.Insert(0, this.gutter);
        this.editor.TextArea.TextView.BackgroundRenderers.Add(new RowShading(this));
        this.finder = new EditorFind(this.editor, this);
        global::Avalonia.Automation.AutomationProperties.SetName(this.editor, "Read-only file comparison");
        this.status.Classes.Add("diff-status");
        Panel content = new();
        content.Children.Add(this.editor);
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
    public int Find(string query) => this.finder.Find(query);

    /// <summary>Selects the match after the current one, wrapping to the first.</summary>
    public void FindNext() => this.finder.FindNext();

    /// <summary>Selects the match before the current one, wrapping to the last.</summary>
    public void FindPrevious() => this.finder.FindPrevious();

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.finder.CurrentMatch;

    /// <summary>The hunks shown, after the latest comparison finished.</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; private set; } = [];

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OldTextProperty || change.Property == NewTextProperty) this.Compare();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        this.computing?.Cancel();
        base.OnDetachedFromVisualTree(e);
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
            try { return (Hunks: TextDiff.Compute(before, after, run.Token), Error: (string?)null); }
            catch (IOException error) { return (Hunks: (IReadOnlyList<DiffHunk>)[], Error: error.Message); }
        }, run.Token).ContinueWith(done =>
        {
            if (done.IsCanceled || run.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(this.computing, run)) this.Show(done.Result.Hunks, done.Result.Error);
            });
        }, TaskScheduler.Default);
    }

    private void Show(IReadOnlyList<DiffHunk> hunks, string? error)
    {
        this.Hunks = hunks;
        List<DiffRow> shown = [];
        StringBuilder text = new();
        void Line(DiffRow row, string content)
        {
            if (shown.Count > 0) text.Append('\n');
            text.Append(content);
            shown.Add(row);
        }
        foreach (DiffHunk hunk in hunks)
        {
            Line(new(DiffRowKind.Header, null, null), string.Create(CultureInfo.InvariantCulture,
                $"@@ -{hunk.OldStartLine},{hunk.OldCount} +{hunk.NewStartLine},{hunk.NewCount} @@"));
            foreach (DiffLine line in hunk.Lines)
            {
                Line(new(line.Kind switch { DiffLineKind.Add => DiffRowKind.Add, DiffLineKind.Remove => DiffRowKind.Remove, _ => DiffRowKind.Context },
                    line.OldLine, line.NewLine), line.Text);
                if (line.Ending == LineEnding.None) Line(new(DiffRowKind.NoNewline, null, null), "No final newline");
            }
        }
        this.rows = shown;
        this.finder.Clear();
        this.editor.Document.Text = text.ToString();
        this.editor.IsVisible = shown.Count > 0;
        this.status.Text = error ?? "No text differences.";
        this.status.IsVisible = shown.Count == 0;
        this.gutter.InvalidateMeasure();
        this.editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private enum DiffRowKind { Header, Context, Add, Remove, NoNewline }

    private sealed record DiffRow(DiffRowKind Kind, int? OldLine, int? NewLine);

    /// <summary>The old and new line numbers and the plus or minus mark beside each row.</summary>
    private sealed class DiffGutter(Func<IReadOnlyList<DiffRow>> rows) : AbstractMargin
    {
        private const double Padding = 6;

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= this.Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += this.Redraw;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int widest = rows().Select(row => Math.Max(row.OldLine ?? 0, row.NewLine ?? 0)).DefaultIfEmpty(0).Max();
            double digit = this.Text("0", Brushes.Black).Width;
            int digits = Math.Max(2, widest.ToString(CultureInfo.InvariantCulture).Length);
            return new Size((digit * digits * 2) + digit + (Padding * 4), 0);
        }

        public override void Render(DrawingContext context)
        {
            TextView? view = this.TextView;
            if (view is null || !view.VisualLinesValid) return;
            IReadOnlyList<DiffRow> shown = rows();
            IBrush muted = CodeEditors.Brush(this, "muted.brush", Brushes.Gray);
            double digit = this.Text("0", muted).Width;
            double column = (this.Bounds.Width - digit - (Padding * 4)) / 2;
            foreach (VisualLine line in view.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= shown.Count) continue;
                DiffRow row = shown[index];
                double y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                if (row.OldLine is { } old) this.Right(context, old, muted, Padding + column, y);
                if (row.NewLine is { } added) this.Right(context, added, muted, (Padding * 2) + (column * 2), y);
                string mark = row.Kind switch { DiffRowKind.Add => "+", DiffRowKind.Remove => "−", _ => string.Empty };
                if (mark.Length > 0) context.DrawText(this.Text(mark, muted), new Point((Padding * 3) + (column * 2), y));
            }
        }

        private void Right(DrawingContext context, int number, IBrush brush, double right, double y)
        {
            FormattedText text = this.Text(number.ToString(CultureInfo.InvariantCulture), brush);
            context.DrawText(text, new Point(right - text.Width, y));
        }

        private FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(this.TextView?.GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default),
            this.TextView?.GetValue(TextBlock.FontSizeProperty) ?? 12, brush);

        private void Redraw(object? sender, EventArgs args) => this.InvalidateVisual();
    }

    /// <summary>Shades added and removed rows and the hunk headers across the full width.</summary>
    private sealed class RowShading(DiffView owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            IBrush added = CodeEditors.Brush(owner, "ok.brush.15", new SolidColorBrush(Colors.Green, 0.15));
            IBrush removed = CodeEditors.Brush(owner, "danger.brush.15", new SolidColorBrush(Colors.Red, 0.15));
            IBrush header = CodeEditors.Brush(owner, "raised.brush", new SolidColorBrush(Colors.Gray, 0.15));
            foreach (VisualLine line in textView.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= owner.rows.Count) continue;
                IBrush? brush = owner.rows[index].Kind switch
                {
                    DiffRowKind.Add => added,
                    DiffRowKind.Remove => removed,
                    DiffRowKind.Header => header,
                    _ => null,
                };
                if (brush is null) continue;
                double top = line.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(brush, new Rect(0, top, Math.Max(textView.Bounds.Width, ((global::Avalonia.Controls.Primitives.IScrollable)textView).Extent.Width), line.Height));
            }
        }
    }
}
