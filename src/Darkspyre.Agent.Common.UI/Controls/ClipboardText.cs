using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Writes literal text to the clipboard of the window a control is in.</summary>
public static class ClipboardText
{
    /// <summary>Writes text to the window's clipboard; false when no clipboard is available.</summary>
    /// <param name="source">A control in the window whose clipboard receives the text.</param>
    /// <param name="text">The exact text to write.</param>
    /// <gotcha>False also covers a view that is not in a window and a write the platform refuses; it shows nothing.</gotcha>
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
