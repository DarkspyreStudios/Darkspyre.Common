using System.Text;
using Darkspyre.Common.Avalonia.Terminal;
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

    private static string Screen(TerminalView view) =>
        Assert.Single(ViewSession.All<TerminalControl>(view)).TryExportSnapshot(TerminalSnapshotExportFormat.PlainText, default, out string text) ? text : string.Empty;
}
