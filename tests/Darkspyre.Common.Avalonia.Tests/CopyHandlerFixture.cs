using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Common.Avalonia.Tests;

/// <summary>
/// The stand-in for the application's clipboard service in one test window: it handles every copy request that reaches
/// the window, writes it through <see cref="TextCopy.WriteAsync"/> and completes with the result, or completes false
/// when <see cref="Refuse"/> is set. Darkspyre.Common cannot reference the agent-ds service, so this is the one seam.
/// </summary>
internal sealed class CopyHandlerFixture
{
    private CopyHandlerFixture(TopLevel window)
    {
        window.AddHandler(TextCopy.RequestedEvent, (_, request) =>
        {
            this.Requests.Add(request.Text);
            request.Completion = this.Refuse ? Task.FromResult(false) : TextCopy.WriteAsync(window, request.Text);
            request.Handled = true;
        });
    }

    /// <summary>Every text a request asked to copy, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>True to refuse every request, as a clipboard that refuses the write does.</summary>
    public bool Refuse { get; set; }

    /// <summary>Handles the copy requests of <paramref name="window"/>.</summary>
    public static CopyHandlerFixture Attach(TopLevel window) => new(window);

    /// <summary>The window's clipboard text.</summary>
    public static async Task<string?> ClipboardAsync(TopLevel window) => await window.Clipboard!.TryGetTextAsync();
}

/// <summary>Real pointer and key input for the selection tests.</summary>
internal static class Input
{
    /// <summary>The platform's edit keys in the test session.</summary>
    public static PlatformHotkeyConfiguration Keys => Application.Current!.PlatformSettings!.HotkeyConfiguration;

    /// <summary>A point in <paramref name="window"/> at <paramref name="x"/> across and halfway down <paramref name="control"/>.</summary>
    public static Point At(Visual control, Visual window, double x = 4) => control.TranslatePoint(new Point(x, control.Bounds.Height / 2), window)!.Value;

    /// <summary>A left press at <paramref name="from"/>, moves to <paramref name="to"/>, and a release there.</summary>
    public static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(6, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        ViewSession.Settle();
    }

    /// <summary>A left click at <paramref name="point"/>.</summary>
    public static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        ViewSession.Settle();
    }

    /// <summary>Presses <paramref name="gesture"/>'s key with its modifiers.</summary>
    public static void Press(TopLevel window, KeyGesture gesture)
    {
        window.KeyPress(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        window.KeyRelease(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        ViewSession.Settle();
    }
}
