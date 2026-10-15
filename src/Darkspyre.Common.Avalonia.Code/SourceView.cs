using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Darkspyre.Common.Avalonia.Code;

/// <summary>
/// A read-only source file: line numbers, TextMate highlighting for the file's language, find with every match marked,
/// and a target line it scrolls to.
/// </summary>
/// <gotcha>The highlighter is installed while the view is in a window and removed when it leaves, so a view that is
/// never shown holds no TextMate state.</gotcha>
public sealed class SourceView : TemplatedControl
{
    /// <summary>The text shown.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<SourceView, string?>(nameof(Text));

    /// <summary>The file extension (with its dot) or TextMate language id that selects the highlighting.</summary>
    public static readonly StyledProperty<string?> LanguageProperty = AvaloniaProperty.Register<SourceView, string?>(nameof(Language));

    private readonly TextEditor editor = CodeEditors.Create(lineNumbers: true);
    private readonly List<(int Offset, int Length)> matches = [];
    private RegistryOptions? registry;
    private TextMate.Installation? highlighting;
    private int current = -1;
    private int target;

    /// <summary>Creates the view.</summary>
    public SourceView()
    {
        this.editor.TextArea.TextView.BackgroundRenderers.Add(new RangeMarks(this.Marks));
        this.Template = new FuncControlTemplate<SourceView>((view, _) => view.editor);
        global::Avalonia.Automation.AutomationProperties.SetName(this.editor, "Source");
        this.ActualThemeVariantChanged += (_, _) =>
        {
            if (this.highlighting is not null && this.registry is not null)
                this.highlighting.SetTheme(this.registry.LoadTheme(ThemeFor(this.ActualThemeVariant)));
        };
    }

    /// <summary>The text shown, or null for none.</summary>
    public string? Text
    {
        get => this.GetValue(TextProperty);
        set => this.SetValue(TextProperty, value);
    }

    /// <summary>File extension (such as <c>.cs</c>) or language id for TextMate highlighting; null for plain text.</summary>
    public string? Language
    {
        get => this.GetValue(LanguageProperty);
        set => this.SetValue(LanguageProperty, value);
    }

    /// <summary>Scrolls to the one-based <paramref name="line"/> and puts the caret on it; a line past the end goes to the last line.</summary>
    public void GoToLine(int line)
    {
        this.target = line;
        int count = this.editor.Document.LineCount;
        int number = Math.Clamp(line, 1, Math.Max(1, count));
        this.editor.TextArea.Caret.Line = number;
        this.editor.ScrollToLine(number);
    }

    /// <summary>Marks every case-insensitive match of <paramref name="query"/>, selects the first, and returns how many there are.</summary>
    public int Find(string query)
    {
        this.matches.Clear();
        this.current = -1;
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

    /// <summary>Selects the match after the current one, wrapping to the first.</summary>
    public void FindNext()
    {
        if (this.matches.Count > 0) this.Select((this.current + 1) % this.matches.Count);
    }

    /// <summary>Selects the match before the current one, wrapping to the last.</summary>
    public void FindPrevious()
    {
        if (this.matches.Count > 0) this.Select((this.current - 1 + this.matches.Count) % this.matches.Count);
    }

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.current + 1;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            this.matches.Clear();
            this.current = -1;
            this.editor.Document.Text = this.Text ?? string.Empty;
            if (this.target > 0) this.GoToLine(this.target);
        }
        else if (change.Property == LanguageProperty) this.ApplyGrammar();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        this.registry = new RegistryOptions(ThemeFor(this.ActualThemeVariant));
        this.highlighting = this.editor.InstallTextMate(this.registry);
        this.ApplyGrammar();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        this.highlighting?.Dispose();
        this.highlighting = null;
        this.registry = null;
        base.OnDetachedFromVisualTree(e);
    }

    private static ThemeName ThemeFor(ThemeVariant variant) => variant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    private void ApplyGrammar()
    {
        if (this.highlighting is null || this.registry is null) return;
        string? language = this.Language;
        TextMateSharp.Grammars.Language? found = string.IsNullOrEmpty(language) ? null
            : language.StartsWith('.') ? this.registry.GetLanguageByExtension(language)
            : this.registry.GetAvailableLanguages().FirstOrDefault(item => string.Equals(item.Id, language, StringComparison.OrdinalIgnoreCase));
        this.highlighting.SetGrammar(found is null ? null : this.registry.GetScopeByLanguageId(found.Id));
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
        IBrush match = CodeEditors.Brush(this, "accent.brush.20", new SolidColorBrush(Colors.Gold, 0.35));
        IBrush selected = CodeEditors.Brush(this, "accent.brush.50", new SolidColorBrush(Colors.Orange, 0.6));
        return [.. this.matches.Select((item, index) => (item.Offset, item.Length, index == this.current ? selected : match))];
    }
}
