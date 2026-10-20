using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Darkspyre.Agent.Common.UI.Code;

/// <summary>
/// A read-only source file: line numbers, TextMate highlighting for the file's language, find with every match marked,
/// and a target line it scrolls to. A new text keeps the scroll position.
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
    private readonly EditorFind finder;
    private RegistryOptions? registry;
    private TextMate.Installation? highlighting;
    // A line asked for before any text arrived, applied when the text does.
    private int pending;

    /// <summary>Creates the view.</summary>
    public SourceView()
    {
        this.finder = new EditorFind(this.editor, this);
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
    /// <remarks>Asked for before any text is set, the line is applied when the text arrives.</remarks>
    public void GoToLine(int line)
    {
        this.pending = this.editor.Document.TextLength == 0 ? line : 0;
        int count = this.editor.Document.LineCount;
        int number = Math.Clamp(line, 1, Math.Max(1, count));
        this.editor.TextArea.Caret.Line = number;
        this.editor.ScrollToLine(number);
    }

    /// <summary>Marks every case-insensitive match of <paramref name="query"/>, selects the first, and returns how many there are.</summary>
    public int Find(string query) => this.finder.Find(query);

    /// <summary>Selects the match after the current one, wrapping to the first.</summary>
    public void FindNext() => this.finder.FindNext();

    /// <summary>Selects the match before the current one, wrapping to the last.</summary>
    public void FindPrevious() => this.finder.FindPrevious();

    /// <summary>The one-based index of the selected match, or 0 when none is selected.</summary>
    public int CurrentMatch => this.finder.CurrentMatch;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            this.finder.Clear();
            this.editor.Document.Text = this.Text ?? string.Empty;
            if (this.pending > 0) this.GoToLine(this.pending);
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
}
