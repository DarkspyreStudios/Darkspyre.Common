using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Darkspyre.Agent.Common.UI.Composition;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// Asks before something that cannot be undone. Every way out reports the same way: Escape, Cancel and the backdrop
/// answer no; the action answers yes, with the extra choice when there is one.
/// </summary>
public sealed class ConfirmDialog : IModal
{
    private readonly ModalLayer layer;
    private readonly Border view;
    private readonly CheckBox? extra;
    private readonly TaskCompletionSource<(bool Confirmed, bool Extra)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ConfirmDialog(ModalLayer layer, string title, string? message, Control? details, string confirmation, bool destructive,
        string? extraLabel, SurfaceScopeContext? scope, string cancelLabel)
    {
        this.layer = layer;
        StackPanel layout = new() { Spacing = 12 };
        TextBlock titleText = new() { Text = title };
        titleText.Classes.Add("dialog-title");
        layout.Children.Add(titleText);
        if (message is not null)
        {
            TextBlock text = new() { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            text.Classes.Add("dialog-description");
            layout.Children.Add(text);
        }
        if (details is not null)
        {
            details.Classes.Add("confirm-details");
            layout.Children.Add(details);
        }
        if (extraLabel is not null)
        {
            this.extra = new CheckBox { Content = extraLabel };
            layout.Children.Add(this.extra);
        }
        StackPanel footer = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Classes.Add("dialog-footer");
        Button cancel = new() { Content = cancelLabel };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => this.Finish(false);
        Button go = new() { Content = confirmation };
        go.Classes.Add(destructive ? "destructive" : "primary");
        go.Click += (_, _) => this.Finish(true);
        footer.Children.Add(cancel);
        footer.Children.Add(go);
        // The footer stays in view; a long message scrolls above it.
        DockPanel frame = new();
        DockPanel.SetDock(footer, Dock.Bottom);
        frame.Children.Add(footer);
        frame.Children.Add(new ScrollViewer { Content = layout, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        CompositionTarget target = new() { TargetName = "confirmation", Scope = scope ?? new(), Content = frame };
        target.Classes.Add("dialog-composition-content");
        this.view = new Border { Child = target, Focusable = true };
        this.view.Classes.Add("alert-dialog");
    }

    /// <inheritdoc />
    public Control View => this.view;

    /// <summary>
    /// Asks <paramref name="title"/> and returns whether to go ahead, and the extra choice. The go-ahead button reads
    /// <paramref name="confirmation"/> and is destructive unless the action loses nothing; the other reads
    /// <paramref name="cancelLabel"/>. Cancelling <paramref name="cancellationToken"/> closes the dialog and answers no.
    /// </summary>
    /// <gotcha>Call it on the UI thread. The cancellation is applied on the UI thread, and it never answers yes.</gotcha>
    public static async Task<(bool Confirmed, bool Extra)> AskAsync(ModalLayer layer, string title, string? message = null,
        Control? details = null, string confirmation = "Delete", bool destructive = true, string? extraLabel = null,
        SurfaceScopeContext? scope = null, string cancelLabel = "Cancel", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfirmDialog dialog = new(layer, title, message, details, confirmation, destructive, extraLabel, scope, cancelLabel);
        layer.Show(dialog);
        await using CancellationTokenRegistration cancelled = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(() => dialog.Finish(false)));
        return await dialog.answer.Task.ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void RequestDismiss() => this.Finish(false);

    private void Finish(bool confirmed)
    {
        if (!this.answer.TrySetResult((confirmed, confirmed && this.extra?.IsChecked == true))) return;
        this.layer.Close(this);
    }
}
