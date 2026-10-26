using System.Text;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Darkspyre.Agent.Common.UI.Controls;
using Darkspyre.Agent.Common.UI.Terminal;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;
using Xunit;

namespace Darkspyre.Common.Avalonia.Tests;

public sealed class TerminalViewTests
{
    [Fact]
    public Task WrittenBytesShowOnTheScreen() => ViewSession.RunAsync(async () =>
    {
        using TerminalView view = new();
        ViewSession.Show(view);

        view.Write(Encoding.UTF8.GetBytes("hello \u001b[31mred\u001b[0m world\r\n"));

        await ViewSession.WaitForAsync(() => Screen(view).Contains("hello red world", StringComparison.Ordinal));
    });

    [Fact]
    public Task TypedInputComesOutAsBytes() => ViewSession.RunAsync(async () =>
    {
        using TerminalView view = new();
        ViewSession.Show(view);
        List<byte> typed = [];
        view.InputReceived += (_, bytes) => typed.AddRange(bytes.ToArray());

        Assert.Single(ViewSession.All<TerminalControl>(view)).SendInput("ls\r");

        await ViewSession.WaitForAsync(() => Encoding.UTF8.GetString([.. typed]).Contains("ls", StringComparison.Ordinal));
    });

    [Fact]
    public Task ResizingTheViewReportsTheNewCellSize() => ViewSession.RunAsync(async () =>
    {
        using TerminalView view = new();
        global::Avalonia.Controls.Window window = ViewSession.Show(view, 900, 600);
        TerminalSize before = view.Size;
        List<TerminalSize> reported = [];
        view.ScreenSizeChanged += (_, size) => reported.Add(size);

        window.Width = 450;

        await ViewSession.WaitForAsync(() => reported.Count > 0);
        Assert.True(reported[^1].Columns < before.Columns, $"{before} then {reported[^1]}");
        Assert.Equal(view.Size, reported[^1]);
    });

    /// <summary>
    /// The keys follow the platform's command modifier: with Meta, Cmd+C copies; with Control, Ctrl+Shift+C copies and
    /// Ctrl+C goes to the program. The Copy key with nothing selected copies nothing.
    /// </summary>
    [Fact]
    public Task KeysFollowTheCommandModifier() => ViewSession.RunAsync(async () =>
    {
        TerminalKeys mac = TerminalView.KeysFor(Configuration(KeyModifiers.Meta));
        TerminalKeys other = TerminalView.KeysFor(Configuration(KeyModifiers.Control));
        Assert.Equal((new KeyGesture(Key.C, KeyModifiers.Meta), new KeyGesture(Key.V, KeyModifiers.Meta), new KeyGesture(Key.A, KeyModifiers.Meta)),
            (mac.Copy, mac.Paste, mac.SelectAll));
        KeyModifiers shifted = KeyModifiers.Control | KeyModifiers.Shift;
        Assert.Equal((new KeyGesture(Key.C, shifted), new KeyGesture(Key.V, shifted), new KeyGesture(Key.A, shifted)), (other.Copy, other.Paste, other.SelectAll));

        foreach (TerminalKeys keys in new[] { mac, other })
        {
            using TerminalView view = new(keys);
            global::Avalonia.Controls.Window window = ViewSession.Show(new TextSelectionScope { Child = view });
            CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);
            List<byte> typed = [];
            view.InputReceived += (_, bytes) => typed.AddRange(bytes.ToArray());
            view.Write(Encoding.UTF8.GetBytes("selected words\r\n"));
            await ViewSession.WaitForAsync(() => Screen(view).Contains("selected words", StringComparison.Ordinal));
            TerminalControl terminal = Assert.Single(ViewSession.All<TerminalControl>(view));
            view.FocusScreen();

            Input.Press(window, keys.Copy);
            Assert.Empty(copies.Requests);

            terminal.SelectAll();
            Input.Press(window, keys.Copy);
            await ViewSession.WaitForAsync(() => copies.Requests.Count == 1);
            Assert.Contains("selected words", copies.Requests[0], StringComparison.Ordinal);

            if (ReferenceEquals(keys, other))
            {
                Input.Press(window, new KeyGesture(Key.C, KeyModifiers.Control));
                await ViewSession.WaitForAsync(() => typed.Contains(0x03));
                Assert.Single(copies.Requests);
            }
            window.Close();
        }
    });

    /// <summary>
    /// Select All, the modifier's own key alone, then Copy copies the whole buffer, rows that scrolled above the screen
    /// included.
    /// </summary>
    [Fact]
    public Task SelectAllCopiesTheWholeBuffer() => ViewSession.RunAsync(async () =>
    {
        using TerminalView view = new();
        global::Avalonia.Controls.Window window = ViewSession.Show(new TextSelectionScope { Child = view });
        CopyHandlerFixture copies = CopyHandlerFixture.Attach(window);
        view.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 80).Select(number => $"row {number}\r\n"))));
        await ViewSession.WaitForAsync(() => Screen(view).Contains("row 80", StringComparison.Ordinal));
        Assert.DoesNotContain("row 1\n", Screen(view).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        TerminalKeys keys = TerminalView.KeysFor(Input.Keys);
        view.FocusScreen();

        Input.Press(window, keys.SelectAll);
        Key modifier = Input.Keys.CommandModifiers.HasFlag(KeyModifiers.Meta) ? Key.LWin : Key.LeftCtrl;
        window.KeyPress(modifier, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(modifier, RawInputModifiers.None, PhysicalKey.None, null);
        Input.Press(window, keys.Copy);

        await ViewSession.WaitForAsync(() => copies.Requests.Count == 1);
        string[] rows = copies.Requests[0].Split('\n');
        Assert.Equal("row 1", rows[0].TrimEnd());
        Assert.Contains(rows, row => row.TrimEnd() == "row 80");
    });

    // A configuration with the given command modifier. Avalonia keeps its constructor out of the reference assembly.
    private static PlatformHotkeyConfiguration Configuration(KeyModifiers command) =>
        (PlatformHotkeyConfiguration)Activator.CreateInstance(typeof(PlatformHotkeyConfiguration), command, KeyModifiers.Shift, KeyModifiers.Control)!;

    private static string Screen(TerminalView view) =>
        Assert.Single(ViewSession.All<TerminalControl>(view)).TryExportSnapshot(TerminalSnapshotExportFormat.PlainText, default, out string text) ? text : string.Empty;
}
