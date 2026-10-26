using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Darkspyre.Agent.Common.UI.Controls;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// The window's layer for sheets and confirmations, above the shell. Each open dialog shows over a backdrop; Escape or a
/// click on the backdrop dismisses the topmost one. The shell view places the layer; any view opens a dialog here. Each
/// dialog's view sits in its own <see cref="TextSelectionScope"/>, so a drag and Select All in it stay in the dialog.
/// </summary>
public sealed class ModalLayer : Panel
{
    private readonly List<(Border Backdrop, IModal Dialog)> open = [];

    /// <summary>Creates the layer, which takes no space and no input while no dialog is open.</summary>
    public ModalLayer()
    {
        this.IsHitTestVisible = false;
        this.Classes.Add("modal-layer");
    }

    /// <summary>Shows <paramref name="dialog"/> over everything opened before it.</summary>
    public void Show(IModal dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        // A dialog stays 24 px inside the window on every side; content that does not fit scrolls inside the dialog. A
        // layer that fills the window takes all of it. The backdrop's dimming brush comes from the modal-backdrop style; a
        // local brush here would override it.
        Border backdrop = new() { Padding = new Thickness(dialog.FillsWindow ? 0 : 24) };
        backdrop.Classes.Add("modal-backdrop");
        backdrop.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, backdrop)) dialog.RequestDismiss();
        };
        Control content = dialog.View;
        content.HorizontalAlignment = dialog.FillsWindow ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        content.VerticalAlignment = dialog.FillsWindow ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        // The scope has no background, so a press beside a centred dialog still reaches the backdrop.
        backdrop.Child = new TextSelectionScope { Child = content };
        this.open.Add((backdrop, dialog));
        this.Children.Add(backdrop);
        this.IsHitTestVisible = true;
        content.AttachedToVisualTree += (_, _) => (content as InputElement)?.Focus();
    }

    /// <summary>
    /// Caps each open dialog's width at the room the layer leaves it, before the dialogs are measured. A dialog's size
    /// class sets an exact width, which Avalonia keeps even when the window is narrower; the cap narrows it to the layer
    /// less the backdrop's padding and the dialog's own margin.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach ((Border backdrop, IModal dialog) in this.open)
        {
            if (dialog.FillsWindow) continue;
            Control view = dialog.View;
            double room = availableSize.Width - backdrop.Padding.Left - backdrop.Padding.Right - view.Margin.Left - view.Margin.Right;
            view.MaxWidth = Math.Max(0, room);
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The backdrop of the topmost open dialog, or null when none is open.</summary>
    internal Control? TopDialog => this.open.Count > 0 ? this.open[^1].Backdrop : null;

    /// <summary>Removes <paramref name="dialog"/>; a dialog already gone is ignored.</summary>
    public void Close(IModal dialog)
    {
        int index = this.open.FindIndex(entry => ReferenceEquals(entry.Dialog, dialog));
        if (index < 0) return;
        this.Children.Remove(this.open[index].Backdrop);
        this.open.RemoveAt(index);
        this.IsHitTestVisible = this.open.Count > 0;
    }

    /// <summary>Dismisses the topmost dialog on Escape in <paramref name="window"/>. The application calls it once for its window.</summary>
    public void Attach(TopLevel window) => window.AddHandler(KeyDownEvent, (_, args) =>
    {
        if (args.Key != Key.Escape || this.open.Count == 0) return;
        this.open[^1].Dialog.RequestDismiss();
        args.Handled = true;
    }, RoutingStrategies.Tunnel);
}

/// <summary>A dialog the modal layer shows.</summary>
public interface IModal
{
    /// <summary>The dialog's view.</summary>
    Control View { get; }

    /// <summary>Asks the dialog to close unanswered, as Escape and a click outside do. It may refuse, such as for unsaved edits.</summary>
    void RequestDismiss();

    /// <summary>True for a layer that fills the window: no backdrop padding and no centring.</summary>
    bool FillsWindow => false;
}
