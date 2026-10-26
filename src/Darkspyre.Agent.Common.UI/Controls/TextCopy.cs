using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>A request to put text on the clipboard, raised from the control the text came from.</summary>
/// <param name="text">The exact text to copy.</param>
public sealed class CopyRequestedEventArgs(string text) : RoutedEventArgs(TextCopy.RequestedEvent)
{
    /// <summary>The exact text to copy.</summary>
    public string Text { get; } = text;

    /// <summary>Set by the handler: completes with true once the text is on the clipboard, false when it was refused.</summary>
    public Task<bool>? Completion { get; set; }
}

/// <summary>The one way text leaves the Harness for the clipboard.</summary>
public static class TextCopy
{
    /// <summary>Bubbles from the source control; the application's clipboard service handles it at the window.</summary>
    public static readonly RoutedEvent<CopyRequestedEventArgs> RequestedEvent =
        RoutedEvent.Register<Control, CopyRequestedEventArgs>("CopyRequested", RoutingStrategies.Bubble);

    /// <summary>Raises a copy request for <paramref name="text"/> from <paramref name="source"/>.</summary>
    /// <param name="source">The control the text came from, in a window.</param>
    /// <param name="text">The exact text to copy.</param>
    /// <returns>True once the text is on the clipboard; false when the text is empty, nothing handled the request or the
    /// clipboard refused it.</returns>
    /// <gotcha>Raise it from a control in a window, never from a menu item: a menu lives in a popup, and the request must
    /// reach the window's handler. The selection menu raises it from the control it opened on.</gotcha>
    public static Task<bool> RequestAsync(Control source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(text)) return Task.FromResult(false);
        CopyRequestedEventArgs request = new(text);
        source.RaiseEvent(request);
        return request.Handled && request.Completion is { } completion ? completion : Task.FromResult(false);
    }

    /// <summary>Writes <paramref name="text"/> to the clipboard of <paramref name="source"/>'s window. False when there is no
    /// clipboard or the platform refuses.</summary>
    /// <param name="source">A control in the window whose clipboard receives the text.</param>
    /// <param name="text">The exact text to write.</param>
    /// <gotcha>Only the clipboard service calls this. Every other caller raises <see cref="RequestAsync"/>, so the one
    /// "Copied" feedback shows.</gotcha>
    public static async Task<bool> WriteAsync(Visual source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (TopLevel.GetTopLevel(source)?.Clipboard is not IClipboard clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
