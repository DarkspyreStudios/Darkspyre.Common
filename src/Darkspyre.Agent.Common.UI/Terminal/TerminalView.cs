using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Darkspyre.Agent.Common.UI.Controls;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;

namespace Darkspyre.Agent.Common.UI.Terminal;

/// <summary>The size of a terminal screen in character cells.</summary>
/// <param name="Columns">Columns of cells.</param>
/// <param name="Rows">Rows of cells.</param>
public readonly record struct TerminalSize(int Columns, int Rows);

/// <summary>One gesture each for Copy, Paste and Select All.</summary>
/// <param name="Copy">Copies the selection, or the whole buffer after Select All.</param>
/// <param name="Paste">Sends the clipboard's text to the program.</param>
/// <param name="SelectAll">Selects the whole buffer, scrollback included.</param>
public sealed record TerminalKeys(KeyGesture Copy, KeyGesture Paste, KeyGesture SelectAll);

/// <summary>
/// A VT terminal screen with no process of its own: the host writes the process's output bytes in, and reads the
/// person's input bytes out of <see cref="InputReceived"/>. The screen takes its colours and font from the theme token
/// resources: the panel colour behind the text, the text colour, and the code font and size. Copy, Paste and Select All
/// use <see cref="KeysFor"/> and the menu <see cref="SelectionMenu.Terminal"/>; Select All covers the whole buffer, and
/// every copy goes through <see cref="TextCopy"/>.
/// </summary>
/// <gotcha>Dispose the view when its panel closes. The terminal holds a renderer, glyph caches and a session service
/// that stay alive until then.</gotcha>
public sealed class TerminalView : TemplatedControl, IDisposable, IEditCommandTarget
{
    // Lines kept above the screen; the scrollback the panel offered with xterm.js.
    private const int Scrollback = 10_000;

    // 10,000 rows of about 400 columns: the most the buffer holds at a wide window.
    private const int MaxCopiedCharacters = 4_194_304;

    private readonly HostTransport transport;
    private readonly TerminalControl terminal;

    private bool disposed;
    // Set by Select All, until the next press in the terminal or a key other than the copy and select-all keys.
    private bool allSelected;

    /// <summary>Creates the view, with the keys of the platform's command modifier.</summary>
    /// <gotcha>The application must be running: the keys come from its platform settings.</gotcha>
    public TerminalView()
        : this(KeysFor(Application.Current?.PlatformSettings?.HotkeyConfiguration
            ?? throw new InvalidOperationException("A terminal needs the running application's platform settings.")))
    {
    }

    /// <summary>Creates the view with <paramref name="keys"/> as its Copy, Paste and Select All keys.</summary>
    internal TerminalView(TerminalKeys keys)
    {
        this.Keys = keys;
        this.transport = new HostTransport(bytes => this.InputReceived?.Invoke(this, bytes),
            dimensions => this.ScreenSizeChanged?.Invoke(this, new TerminalSize(dimensions.Columns, dimensions.Rows)));
        // The default control's services, with this view's transport as the only session and no local file reads for images.
        this.terminal = new TerminalControl(new TerminalSessionService(), new DefaultTerminalInputAdapter(), new RoutedTerminalSelection(this),
            new DefaultTerminalScrollService(), new DefaultVtProcessorFactory(new BasicVtProcessorOptions()), new DefaultPtyFactory(),
            new NullSshCredentialProvider(), new KnownHostsSshHostKeyValidator(), new HostTransport.Factory(this.transport))
        {
            Columns = 80,
            Rows = 24,
            ScrollbackLimit = Scrollback,
            // Only Paste stays with RoyalTerminal, on this platform's key: Ctrl+C and Ctrl+V reach the program, and the view
            // runs Copy and Select All itself.
            ShortcutConfiguration = new TerminalShortcutConfiguration(copyGestures: [], pasteGestures: [new(this.Keys.Paste.Key, this.Keys.Paste.KeyModifiers)],
                cutGestures: [], selectAllGestures: []),
            ContextFlyout = SelectionMenu.Terminal,
        };
        this.terminal.AddHandler(KeyDownEvent, this.HandleKeyDown, RoutingStrategies.Tunnel);
        this.terminal.AddHandler(PointerPressedEvent, (_, _) => this.allSelected = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        _ = this.terminal.StartSessionAsync(new HostTransport.Options(new TerminalSessionDimensions(80, 24, 640, 384))).AsTask();
        global::Avalonia.Automation.AutomationProperties.SetName(this.terminal, "Terminal");
        this.Template = new FuncControlTemplate<TerminalView>((view, _) => view.terminal);
        this.Follow("panel", colour => this.terminal.DefaultBackground = colour);
        this.Follow("text", colour => this.terminal.DefaultForeground = colour);
        this.terminal.Bind(TerminalControl.FontFamilyNameProperty, this.GetResourceObservable("font-code", value => value switch
        {
            FontFamily family => family.FamilyNames.Count > 0 ? family.FamilyNames[0] : "Menlo",
            string name => name,
            _ => "Menlo",
        }));
        this.terminal.Bind(TerminalControl.TerminalFontSizeProperty, this.GetResourceObservable("font-size-code", value => value is double size ? size : 13d));
    }

    /// <summary>Raised with the bytes the person typed or pasted, ready for the process's input.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? InputReceived;

    /// <summary>Raised when the screen's size in cells changes, such as when its panel is resized. Control.SizeChanged reports pixels.</summary>
    public event EventHandler<TerminalSize>? ScreenSizeChanged;

    /// <summary>The screen's size in cells.</summary>
    public TerminalSize Size => new(this.terminal.Columns, this.terminal.Rows);

    /// <summary>True while there is a selection to copy, or after Select All.</summary>
    public bool CanCopy => this.allSelected || this.terminal.HasSelection;

    /// <summary>The terminal's Copy, Paste and Select All keys.</summary>
    internal TerminalKeys Keys { get; }

    /// <summary>The terminal's Copy, Paste and Select All keys for this platform: the platform's command modifier with C, V
    /// and A, plus Shift when that modifier is Ctrl, so Ctrl+C, Ctrl+V and Ctrl+A stay with the program running in the
    /// terminal.</summary>
    /// <param name="platform">The platform's key configuration, whose command modifier decides.</param>
    public static TerminalKeys KeysFor(PlatformHotkeyConfiguration platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        KeyModifiers command = platform.CommandModifiers;
        if (command.HasFlag(KeyModifiers.Control)) command |= KeyModifiers.Shift;
        return new(new KeyGesture(Key.C, command), new KeyGesture(Key.V, command), new KeyGesture(Key.A, command));
    }

    /// <summary>Copies the selection through <see cref="TextCopy"/>; after Select All, the whole buffer, scrollback included.
    /// With nothing selected it copies nothing.</summary>
    public void Copy()
    {
        if (this.allSelected) _ = TextCopy.RequestAsync(this, this.WholeBuffer());
        else _ = this.terminal.CopySelectionAsync();
    }

    /// <summary>Selects the whole buffer, scrollback included, for the next Copy; the visible rows show the highlight.</summary>
    public void SelectAll()
    {
        this.allSelected = true;
        this.terminal.SelectAll();
    }

    /// <summary>Sends the clipboard's text to the program.</summary>
    public void Paste() => _ = this.terminal.PasteAsync();

    /// <summary>Writes the process's <paramref name="output"/> to the screen. Any thread may call it.</summary>
    public void Write(ReadOnlySpan<byte> output)
    {
        if (this.disposed || output.IsEmpty) return;
        this.transport.Deliver(output.ToArray());
    }

    /// <summary>Gives the screen keyboard focus.</summary>
    public void FocusScreen() => this.terminal.Focus();

    /// <summary>Releases the terminal's renderer, caches and session service.</summary>
    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.terminal.StopPty();
        this.transport.Dispose();
        (this.terminal.TerminalSessionService as IDisposable)?.Dispose();
    }

    // Every row of the buffer, a line break between rows except where a row wraps onto the next.
    private string WholeBuffer()
    {
        if (this.terminal.GetHistoryBufferInfo(out TerminalHistoryBufferInfo? buffer) != TerminalHistoryStatus.Success || buffer is null) return string.Empty;
        TerminalHistoryCaptureRequest request = new(Math.Max(1, buffer.AvailableRange.Count), MaxCopiedCharacters);
        if (this.terminal.CaptureHistory(in request, out TerminalHistorySnapshot? snapshot) != TerminalHistoryStatus.Success || snapshot is null) return string.Empty;
        System.Text.StringBuilder text = new();
        for (int index = 0; index < snapshot.Rows.Length; index++)
        {
            TerminalHistoryRow row = snapshot.Rows[index];
            text.Append(row.Text);
            if (!row.WrapsToNext && index < snapshot.Rows.Length - 1) text.Append('\n');
        }
        return text.ToString();
    }

    // The view's own Copy and Select All keys, ahead of RoyalTerminal. Any other key ends a Select All, except a modifier
    // pressed alone, so the person can release and press the modifier between the two gestures.
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (this.Keys.Copy.Matches(e))
        {
            this.Copy();
            e.Handled = true;
        }
        else if (this.Keys.SelectAll.Matches(e))
        {
            this.SelectAll();
            e.Handled = true;
        }
        else if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            this.allSelected = false;
    }

    // A resource pass that leaves the colour as it was changes nothing on the screen.
    private void Follow(string token, Action<Color> apply)
    {
        Color? applied = null;
        this.GetResourceObservable(token).Subscribe(new Observer(value =>
        {
            if (value is not Color colour || colour == applied) return;
            applied = colour;
            apply(colour);
        }));
    }

    private sealed class Observer(Action<object?> next) : IObserver<object?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(object? value) => next(value);
    }
}
