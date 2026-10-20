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

namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>How the shared read-only diff is laid out.</summary>
public enum DiffViewMode
{
    /// <summary>One column: a header per hunk, then each line with its old and new line numbers and a plus or minus mark.</summary>
    Unified,
    /// <summary>Two aligned columns: the old text on the left and the new text on the right, row for row.</summary>
    SideBySide,
}

/// <summary>
/// A read-only diff of two texts. <see cref="DiffViewMode.Unified"/> shows a header per hunk, then each line with its
/// old and new line numbers and a plus or minus mark. <see cref="DiffViewMode.SideBySide"/> shows the old text on the
/// left and the new text on the right, aligned row for row, with empty filler rows where one side has fewer lines.
/// Added lines are on the theme's ok colour and removed lines on its danger colour. A last line with no line break is
/// followed by a "No final newline" note. Line breaks take part in the comparison.
/// </summary>
/// <gotcha>
/// A null <see cref="OldText"/> or <see cref="NewText"/> is an absent side, an empty string an empty file. Side by side,
/// an absent side shows its label and "File is absent in this version." instead of rows; unified, both compare as empty.
/// </gotcha>
public sealed class DiffView : TemplatedControl
{
    /// <summary>The message an absent side shows side by side.</summary>
    internal const string AbsentMessage = "File is absent in this version.";

    /// <summary>The old text, or null for none.</summary>
    public static readonly StyledProperty<string?> OldTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(OldText));

    /// <summary>The new text, or null for none.</summary>
    public static readonly StyledProperty<string?> NewTextProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(NewText));

    /// <summary>Defines the <see cref="ViewMode"/> property.</summary>
    public static readonly StyledProperty<DiffViewMode> ViewModeProperty =
        AvaloniaProperty.Register<DiffView, DiffViewMode>(nameof(ViewMode), DiffViewMode.Unified);

    /// <summary>Defines the <see cref="OldLabel"/> property.</summary>
    public static readonly StyledProperty<string?> OldLabelProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(OldLabel));

    /// <summary>Defines the <see cref="NewLabel"/> property.</summary>
    public static readonly StyledProperty<string?> NewLabelProperty = AvaloniaProperty.Register<DiffView, string?>(nameof(NewLabel));

    private readonly Decorator root = new();
    private readonly TextBlock status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly Unified unified;
    private readonly SideBySide sideBySide;
    private IReadOnlyList<DiffHunk> shownHunks = [];
    private (bool Old, bool New) absent;
    private string? error;
    private CancellationTokenSource? computing;

    /// <summary>Creates the view.</summary>
    public DiffView()
    {
        this.status.Classes.Add("diff-status");
        this.unified = new Unified(this);
        this.sideBySide = new SideBySide(this);
        Panel content = new();
        content.Children.Add(this.root);
        content.Children.Add(this.status);
        this.Template = new FuncControlTemplate<DiffView>((_, _) => content);
        this.Show([], (false, false), null);
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

    /// <summary>Whether the diff shows one unified column or two aligned columns. Default <see cref="DiffViewMode.Unified"/>.</summary>
    public DiffViewMode ViewMode
    {
        get => this.GetValue(ViewModeProperty);
        set => this.SetValue(ViewModeProperty, value);
    }

    /// <summary>What the old side is, such as "Staged"; it heads the left column, or the header line when unified.</summary>
    public string? OldLabel
    {
        get => this.GetValue(OldLabelProperty);
        set => this.SetValue(OldLabelProperty, value);
    }

    /// <summary>What the new side is, such as "Working tree"; it heads the right column, or the header line when unified.</summary>
    public string? NewLabel
    {
        get => this.GetValue(NewLabelProperty);
        set => this.SetValue(NewLabelProperty, value);
    }

    /// <summary>Marks every case-insensitive match of <paramref name="query"/> in the shown diff, selects the first, and returns how many there are.</summary>
    /// <remarks>Side by side, a row the two columns show identically counts its matches once.</remarks>
    public int Find(string query) => this.ViewMode == DiffViewMode.SideBySide ? this.sideBySide.Find(query) : this.unified.Finder.Find(query);

    /// <summary>Selects the match after the current one, wrapping to the first.</summary>
    public void FindNext()
    {
        if (this.ViewMode == DiffViewMode.SideBySide) this.sideBySide.FindNext();
        else this.unified.Finder.FindNext();
    }

    /// <summary>Selects the match before the current one, wrapping to the last.</summary>
    public void FindPrevious()
    {
        if (this.ViewMode == DiffViewMode.SideBySide) this.sideBySide.FindPrevious();
        else this.unified.Finder.FindPrevious();
    }

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.ViewMode == DiffViewMode.SideBySide ? this.sideBySide.CurrentMatch : this.unified.Finder.CurrentMatch;

    /// <summary>The hunks shown, after the latest comparison finished.</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; private set; } = [];

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OldTextProperty || change.Property == NewTextProperty) this.Compare();
        else if (change.Property == ViewModeProperty) this.Show(this.shownHunks, this.absent, this.error);
        else if (change.Property == OldLabelProperty || change.Property == NewLabelProperty) this.ShowLabels();
    }

    // The comparison runs off the UI thread; a newer pair of texts cancels it and only the latest result is shown.
    private void Compare()
    {
        this.computing?.Cancel();
        CancellationTokenSource run = new();
        this.computing = run;
        (bool Old, bool New) missing = (this.OldText is null, this.NewText is null);
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
                if (ReferenceEquals(this.computing, run)) this.Show(done.Result.Hunks, missing, done.Result.Error);
            });
        }, TaskScheduler.Default);
    }

    private void Show(IReadOnlyList<DiffHunk> hunks, (bool Old, bool New) missing, string? failure)
    {
        this.shownHunks = hunks;
        this.absent = missing;
        this.error = failure;
        this.Hunks = hunks;
        bool any;
        if (this.ViewMode == DiffViewMode.SideBySide)
        {
            any = this.sideBySide.Show(hunks, missing) || missing.Old || missing.New;
            this.root.Child = this.sideBySide.View;
        }
        else
        {
            any = this.unified.Show(hunks);
            this.root.Child = this.unified.View;
        }
        this.ShowLabels();
        this.root.IsVisible = any;
        this.status.Text = failure ?? "No text differences.";
        this.status.IsVisible = !any;
    }

    private void ShowLabels()
    {
        this.unified.ShowLabels(this.OldLabel, this.NewLabel);
        this.sideBySide.ShowLabels(this.OldLabel, this.NewLabel);
    }

    private enum DiffRowKind { Header, Context, Add, Remove, NoNewline }

    private sealed record DiffRow(DiffRowKind Kind, int? OldLine, int? NewLine);

    private static string HunkHeader(DiffHunk hunk) => string.Create(CultureInfo.InvariantCulture,
        $"@@ -{hunk.OldStartLine},{hunk.OldCount} +{hunk.NewStartLine},{hunk.NewCount} @@");

    private static TextBlock Label()
    {
        TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
        label.Classes.Add("diff-label");
        return label;
    }

    /// <summary>The one-column layout: a label line, then the unified rows with their two line numbers.</summary>
    private sealed class Unified
    {
        private readonly TextEditor editor = CodeEditors.Create(lineNumbers: false);
        private readonly TextBlock label = Label();
        private readonly DiffGutter gutter;
        private List<DiffRow> rows = [];

        public Unified(DiffView owner)
        {
            this.gutter = new DiffGutter(() => this.rows);
            this.editor.TextArea.LeftMargins.Insert(0, this.gutter);
            this.editor.TextArea.TextView.BackgroundRenderers.Add(new RowShading(owner, index =>
                index < this.rows.Count ? this.rows[index].Kind : null));
            this.Finder = new EditorFind(this.editor, owner);
            global::Avalonia.Automation.AutomationProperties.SetName(this.editor, "Read-only file comparison");
            DockPanel layout = new();
            DockPanel.SetDock(this.label, Dock.Top);
            layout.Children.Add(this.label);
            layout.Children.Add(this.editor);
            this.View = layout;
        }

        public Control View { get; }

        public EditorFind Finder { get; }

        public bool Show(IReadOnlyList<DiffHunk> hunks)
        {
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
                Line(new(DiffRowKind.Header, null, null), HunkHeader(hunk));
                foreach (DiffLine line in hunk.Lines)
                {
                    Line(new(line.Kind switch { DiffLineKind.Add => DiffRowKind.Add, DiffLineKind.Remove => DiffRowKind.Remove, _ => DiffRowKind.Context },
                        line.OldLine, line.NewLine), line.Text);
                    if (line.Ending == LineEnding.None) Line(new(DiffRowKind.NoNewline, null, null), "No final newline");
                }
            }
            this.rows = shown;
            this.Finder.Clear();
            this.editor.Document.Text = text.ToString();
            this.gutter.InvalidateMeasure();
            this.editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            return shown.Count > 0;
        }

        public void ShowLabels(string? old, string? @new)
        {
            this.label.Text = old is null && @new is null ? null : $"{old ?? string.Empty} → {@new ?? string.Empty}";
            this.label.IsVisible = this.label.Text is not null;
        }
    }

    /// <summary>One cell of a side-by-side row; a row's missing cell is a filler.</summary>
    private sealed record SideCell(DiffRowKind Kind, int? Line, string Text);

    /// <summary>One aligned row: the old cell on the left and the new cell on the right, either of which may be a filler.</summary>
    private sealed record SideRow(SideCell? Old, SideCell? New)
    {
        // A row both columns show identically: its matches count once.
        public bool Shared => this.Old is { } old && this.New is { } added && old.Kind == added.Kind && old.Text == added.Text;
    }

    /// <summary>The two-column layout: a labelled column per side, scrolled together row by row, with one find list.</summary>
    private sealed class SideBySide
    {
        private readonly DiffView owner;
        private readonly Column old;
        private readonly Column added;
        private readonly List<Match> matches = [];
        private List<SideRow> rows = [];
        private int current = -1;
        private bool syncing;

        public SideBySide(DiffView owner)
        {
            this.owner = owner;
            this.old = new Column(this, isOld: true);
            this.added = new Column(this, isOld: false);
            this.Follow(this.old, this.added);
            this.Follow(this.added, this.old);
            Border divider = new() { Width = 1 };
            divider.Classes.Add("diff-divider");
            CodeEditors.Bind(divider, Border.BackgroundProperty, "line.brush");
            Grid layout = new() { ColumnDefinitions = new("*,Auto,*") };
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(this.added.View, 2);
            layout.Children.Add(this.old.View);
            layout.Children.Add(divider);
            layout.Children.Add(this.added.View);
            this.View = layout;
        }

        public Control View { get; }

        public int CurrentMatch => this.current + 1;

        public bool Show(IReadOnlyList<DiffHunk> hunks, (bool Old, bool New) absent)
        {
            this.rows = Align(hunks);
            this.matches.Clear();
            this.current = -1;
            this.old.Show(this.rows, absent.Old);
            this.added.Show(this.rows, absent.New);
            return this.rows.Count > 0;
        }

        public void ShowLabels(string? oldLabel, string? newLabel)
        {
            this.old.ShowLabel(oldLabel);
            this.added.ShowLabel(newLabel);
        }

        public int Find(string query)
        {
            this.matches.Clear();
            this.current = -1;
            if (!string.IsNullOrEmpty(query))
            {
                for (int row = 0; row < this.rows.Count; row++)
                {
                    SideRow shown = this.rows[row];
                    this.Collect(query, row, shown.Old, isOld: true, mirrored: shown.Shared);
                    if (!shown.Shared) this.Collect(query, row, shown.New, isOld: false, mirrored: false);
                }
            }
            if (this.matches.Count > 0) this.Select(0);
            this.old.Redraw();
            this.added.Redraw();
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

        // Context on both sides; a removed and an added run between two context lines paired by position, with fillers
        // on the shorter side; a "No final newline" row after a line that ends without a line break.
        private static List<SideRow> Align(IReadOnlyList<DiffHunk> hunks)
        {
            List<SideRow> aligned = [];
            foreach (DiffHunk hunk in hunks)
            {
                string header = HunkHeader(hunk);
                aligned.Add(new(new(DiffRowKind.Header, null, header), new(DiffRowKind.Header, null, header)));
                List<DiffLine> removed = [], addedLines = [];
                void Flush()
                {
                    for (int index = 0; index < Math.Max(removed.Count, addedLines.Count); index++)
                    {
                        DiffLine? left = index < removed.Count ? removed[index] : null;
                        DiffLine? right = index < addedLines.Count ? addedLines[index] : null;
                        aligned.Add(new(left is null ? null : new(DiffRowKind.Remove, left.OldLine, left.Text),
                            right is null ? null : new(DiffRowKind.Add, right.NewLine, right.Text)));
                    }
                    bool oldEnds = removed.Count > 0 && removed[^1].Ending == LineEnding.None;
                    bool newEnds = addedLines.Count > 0 && addedLines[^1].Ending == LineEnding.None;
                    if (oldEnds || newEnds) aligned.Add(new(oldEnds ? NoNewline() : null, newEnds ? NoNewline() : null));
                    removed.Clear();
                    addedLines.Clear();
                }
                foreach (DiffLine line in hunk.Lines)
                {
                    if (line.Kind == DiffLineKind.Remove) removed.Add(line);
                    else if (line.Kind == DiffLineKind.Add) addedLines.Add(line);
                    else
                    {
                        Flush();
                        aligned.Add(new(new(DiffRowKind.Context, line.OldLine, line.Text), new(DiffRowKind.Context, line.NewLine, line.Text)));
                        if (line.Ending == LineEnding.None) aligned.Add(new(NoNewline(), NoNewline()));
                    }
                }
                Flush();
            }
            return aligned;
        }

        private static SideCell NoNewline() => new(DiffRowKind.NoNewline, null, "No final newline");

        private void Collect(string query, int row, SideCell? cell, bool isOld, bool mirrored)
        {
            if (cell is null) return;
            for (int at = cell.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0;
                at = cell.Text.IndexOf(query, at + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase))
                this.matches.Add(new(row, isOld, mirrored, at, query.Length));
        }

        private void Select(int index)
        {
            this.current = index;
            Match match = this.matches[index];
            (match.IsOld ? this.old : this.added).Select(match);
            this.old.Redraw();
            this.added.Redraw();
        }

        private List<(int Offset, int Length, IBrush Brush)> Marks(Column column)
        {
            if (this.matches.Count == 0) return [];
            IBrush match = CodeEditors.Brush(this.owner, "accent.brush.20", new SolidColorBrush(Colors.Gold, 0.35));
            IBrush selected = CodeEditors.Brush(this.owner, "accent.brush.50", new SolidColorBrush(Colors.Orange, 0.6));
            List<(int Offset, int Length, IBrush Brush)> marks = [];
            for (int index = 0; index < this.matches.Count; index++)
            {
                Match found = this.matches[index];
                if (found.IsOld != column.IsOld && !found.Mirrored) continue;
                marks.Add((column.Offset(found.Row) + found.Column, found.Length, index == this.current ? selected : match));
            }
            return marks;
        }

        // Scrolling one column scrolls the other to the same row; every row is one line in both documents.
        private void Follow(Column source, Column target) => source.Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            if (this.syncing) return;
            this.syncing = true;
            try { target.Editor.ScrollToVerticalOffset(source.Editor.TextArea.TextView.VerticalOffset); }
            finally { this.syncing = false; }
        };

        private sealed record Match(int Row, bool IsOld, bool Mirrored, int Column, int Length);

        /// <summary>One side: its label, its rows with one line number and mark each, or the absent note.</summary>
        private sealed class Column
        {
            private readonly SideBySide owner;
            private readonly TextBlock label = Label();
            private readonly TextBlock absentNote = new() { Text = AbsentMessage, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
            private readonly SideGutter gutter;
            private IReadOnlyList<SideRow> rows = [];
            private int[] offsets = [];

            public Column(SideBySide owner, bool isOld)
            {
                this.owner = owner;
                this.IsOld = isOld;
                this.Editor = CodeEditors.Create(lineNumbers: false);
                this.gutter = new SideGutter(() => this.rows, isOld);
                this.Editor.TextArea.LeftMargins.Insert(0, this.gutter);
                this.Editor.TextArea.TextView.BackgroundRenderers.Add(new RowShading(owner.owner, index =>
                    index < this.rows.Count ? this.Cell(this.rows[index])?.Kind : null));
                this.Editor.TextArea.TextView.BackgroundRenderers.Add(new RangeMarks(() => owner.Marks(this)));
                global::Avalonia.Automation.AutomationProperties.SetName(this.Editor,
                    isOld ? "Read-only comparison, old side" : "Read-only comparison, new side");
                this.absentNote.Classes.Add("diff-status");
                Panel body = new();
                body.Children.Add(this.Editor);
                body.Children.Add(this.absentNote);
                DockPanel layout = new();
                DockPanel.SetDock(this.label, Dock.Top);
                layout.Children.Add(this.label);
                layout.Children.Add(body);
                this.View = layout;
            }

            public bool IsOld { get; }

            public TextEditor Editor { get; }

            public Control View { get; }

            public void Show(IReadOnlyList<SideRow> shown, bool absent)
            {
                this.rows = shown;
                StringBuilder text = new();
                this.offsets = new int[shown.Count];
                for (int index = 0; index < shown.Count; index++)
                {
                    if (index > 0) text.Append('\n');
                    this.offsets[index] = text.Length;
                    text.Append(this.Cell(shown[index])?.Text);
                }
                this.Editor.Document.Text = text.ToString();
                this.Editor.IsVisible = !absent;
                this.absentNote.IsVisible = absent;
                this.gutter.InvalidateMeasure();
                this.Redraw();
            }

            public void ShowLabel(string? text)
            {
                this.label.Text = text;
                this.label.IsVisible = text is not null;
            }

            public int Offset(int row) => this.offsets[row];

            public void Select(Match match)
            {
                int offset = this.Offset(match.Row) + match.Column;
                this.Editor.Select(offset, match.Length);
                AvaloniaEdit.Document.TextLocation location = this.Editor.Document.GetLocation(offset);
                this.Editor.ScrollTo(location.Line, location.Column);
            }

            public void Redraw()
            {
                this.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                this.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            }

            private SideCell? Cell(SideRow row) => this.IsOld ? row.Old : row.New;
        }
    }

    /// <summary>The old and new line numbers and the plus or minus mark beside each unified row.</summary>
    private sealed class DiffGutter(Func<IReadOnlyList<DiffRow>> rows) : GutterBase
    {
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
                string mark = Mark(row.Kind);
                if (mark.Length > 0) context.DrawText(this.Text(mark, muted), new Point((Padding * 3) + (column * 2), y));
            }
        }
    }

    /// <summary>The one line number and the plus or minus mark beside each row of one side-by-side column.</summary>
    private sealed class SideGutter(Func<IReadOnlyList<SideRow>> rows, bool isOld) : GutterBase
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            int widest = rows().Select(row => (isOld ? row.Old : row.New)?.Line ?? 0).DefaultIfEmpty(0).Max();
            double digit = this.Text("0", Brushes.Black).Width;
            int digits = Math.Max(2, widest.ToString(CultureInfo.InvariantCulture).Length);
            return new Size((digit * digits) + digit + (Padding * 3), 0);
        }

        public override void Render(DrawingContext context)
        {
            TextView? view = this.TextView;
            if (view is null || !view.VisualLinesValid) return;
            IReadOnlyList<SideRow> shown = rows();
            IBrush muted = CodeEditors.Brush(this, "muted.brush", Brushes.Gray);
            double digit = this.Text("0", muted).Width;
            double column = this.Bounds.Width - digit - (Padding * 3);
            foreach (VisualLine line in view.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= shown.Count || (isOld ? shown[index].Old : shown[index].New) is not { } cell) continue;
                double y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                if (cell.Line is { } number) this.Right(context, number, muted, Padding + column, y);
                string mark = Mark(cell.Kind);
                if (mark.Length > 0) context.DrawText(this.Text(mark, muted), new Point((Padding * 2) + column, y));
            }
        }
    }

    private static string Mark(DiffRowKind kind) => kind switch { DiffRowKind.Add => "+", DiffRowKind.Remove => "−", _ => string.Empty };

    /// <summary>The text drawing both gutters share, in the editor's code font.</summary>
    private abstract class GutterBase : AbstractMargin
    {
        protected const double Padding = 6;

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= this.Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += this.Redraw;
        }

        protected void Right(DrawingContext context, int number, IBrush brush, double right, double y)
        {
            FormattedText text = this.Text(number.ToString(CultureInfo.InvariantCulture), brush);
            context.DrawText(text, new Point(right - text.Width, y));
        }

        protected FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(this.TextView?.GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default),
            this.TextView?.GetValue(TextBlock.FontSizeProperty)
                ?? (this.TryFindResource("font-size-code", out object? size) && size is double code ? code : this.GetValue(TextBlock.FontSizeProperty)), brush);

        private void Redraw(object? sender, EventArgs args) => this.InvalidateVisual();
    }

    /// <summary>Shades added and removed rows and the hunk headers across the full width.</summary>
    private sealed class RowShading(DiffView owner, Func<int, DiffRowKind?> kindOf) : IBackgroundRenderer
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
                if (index < 0) continue;
                IBrush? brush = kindOf(index) switch
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
