using Avalonia.Controls;
using Avalonia.Input.Platform;
using Darkspyre.Agent.Common.UI.Controls;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal.Services;

namespace Darkspyre.Agent.Common.UI.Terminal;

/// <summary>
/// RoyalTerminal's selection service with its copy routed through <see cref="TextCopy"/>, so a terminal copy shows the one
/// "Copied" feedback. RoyalTerminal 0.6.3 has no public read of the selected text, so its own write runs and its text is
/// read back from the clipboard.
/// </summary>
internal sealed class RoutedTerminalSelection(Control owner) : ITerminalSelectionService
{
    private readonly DefaultTerminalSelectionService inner = new();

    /// <summary>Copies the selection; with nothing selected it writes nothing and requests no copy.</summary>
    public async Task CopySelectionAsync(Control control, ITerminalSessionService sessionService, TerminalScreen? screen, SkiaTerminalRenderer? renderer)
    {
        if (control is not TerminalControl { HasSelection: true }) return;
        await this.inner.CopySelectionAsync(control, sessionService, screen, renderer);
        if (TopLevel.GetTopLevel(control)?.Clipboard is not IClipboard clipboard) return;
        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) await TextCopy.RequestAsync(owner, text);
    }

    public Task PasteAsync(Control control, Action<string> sendInput) => this.inner.PasteAsync(control, sendInput);

    public Task PasteAsync(Control control, Action<string> sendInput, TerminalPasteRequest request) => this.inner.PasteAsync(control, sendInput, request);

    public void ClearSelection(TerminalScreen? screen, SkiaTerminalRenderer? renderer, TerminalPresenter? presenter) =>
        this.inner.ClearSelection(screen, renderer, presenter);
}
