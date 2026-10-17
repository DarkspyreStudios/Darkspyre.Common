using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;

namespace Darkspyre.Common.Avalonia.Terminal;

/// <summary>The size of a terminal screen in character cells.</summary>
/// <param name="Columns">Columns of cells.</param>
/// <param name="Rows">Rows of cells.</param>
public readonly record struct TerminalSize(int Columns, int Rows);

/// <summary>
/// A VT terminal screen with no process of its own: the host writes the process's output bytes in, and reads the
/// person's input bytes out of <see cref="InputReceived"/>. The screen takes its colours and font from the theme token
/// resources: the panel colour behind the text, the text colour, and the code font and size.
/// </summary>
/// <gotcha>Dispose the view when its panel closes. The terminal holds a renderer, glyph caches and a session service
/// that stay alive until then.</gotcha>
public sealed class TerminalView : TemplatedControl, IDisposable
{
    // Lines kept above the screen; the scrollback the panel offered with xterm.js.
    private const int Scrollback = 10_000;

    private readonly HostTransport transport;
    private readonly TerminalControl terminal;

    private bool disposed;

    /// <summary>Creates the view.</summary>
    public TerminalView()
    {
        this.transport = new HostTransport(bytes => this.InputReceived?.Invoke(this, bytes),
            dimensions => this.ScreenSizeChanged?.Invoke(this, new TerminalSize(dimensions.Columns, dimensions.Rows)));
        // The default control's services, with this view's transport as the only session and no local file reads for images.
        this.terminal = new TerminalControl(new TerminalSessionService(), new DefaultTerminalInputAdapter(), new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(), new DefaultVtProcessorFactory(new BasicVtProcessorOptions()), new DefaultPtyFactory(),
            new NullSshCredentialProvider(), new KnownHostsSshHostKeyValidator(), new HostTransport.Factory(this.transport))
        {
            Columns = 80,
            Rows = 24,
            ScrollbackLimit = Scrollback,
        };
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

    private void Follow(string token, Action<Color> apply) =>
        this.GetResourceObservable(token).Subscribe(new Observer(value =>
        {
            if (value is Color colour) apply(colour);
        }));

    private sealed class Observer(Action<object?> next) : IObserver<object?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(object? value) => next(value);
    }
}
