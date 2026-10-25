using Avalonia.Controls;
using Avalonia.Threading;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>The content of a sheet a plugin opens.</summary>
/// <param name="Title">The sheet's title.</param>
/// <param name="Content">The sheet's body; it scrolls when it does not fit.</param>
/// <param name="Footer">The sheet's footer, such as its buttons, or null.</param>
/// <param name="Description">A line under the title, or null.</param>
/// <param name="CanDismiss">Asked before Escape, the close button or a click outside closes the sheet; false keeps it open, such as for unsaved edits. Null always allows it.</param>
public sealed record SheetRequest(string Title, Control Content, Control? Footer = null,
    string? Description = null, Func<bool>? CanDismiss = null)
{
    /// <summary>The sheet's width. Defaults to <see cref="DialogSize.Medium"/>.</summary>
    /// <gotcha>Hosts older than SDK 0.14.1 do not have it; a plugin that sets it needs minimumHostSdkVersion 0.14.1.</gotcha>
    public DialogSize Size { get; init; } = DialogSize.Medium;
}

/// <summary>One open sheet. Disposing it closes the sheet.</summary>
public interface ISheet : IAsyncDisposable
{
    /// <summary>True after Complete; false after dismissal, cancellation or plugin retirement.</summary>
    Task<bool> Completion { get; }

    /// <summary>Closes the sheet as answered: <see cref="Completion"/> becomes true.</summary>
    void Complete();

    /// <summary>Asks the sheet to close unanswered, as Escape does; <see cref="SheetRequest.CanDismiss"/> may refuse.</summary>
    void RequestDismiss();
}

/// <summary>One confirmation. Details is an optional control under the message.</summary>
/// <param name="Title">The question, such as "Delete file".</param>
/// <param name="Message">The sentence under the title.</param>
/// <param name="Confirmation">The go-ahead button's label.</param>
/// <param name="Destructive">True when going ahead loses something: the button takes the destructive style.</param>
/// <param name="Details">A control under the message, such as an input the answer uses, or null.</param>
public sealed record ConfirmationRequest(string Title, string Message, string Confirmation = "Delete",
    bool Destructive = true, Control? Details = null)
{
    /// <summary>The confirmation's width. Defaults to <see cref="DialogSize.Narrow"/>.</summary>
    /// <gotcha>Hosts older than SDK 0.14.1 do not have it; a plugin that sets it needs minimumHostSdkVersion 0.14.1.</gotcha>
    public DialogSize Size { get; init; } = DialogSize.Narrow;
}

/// <summary>Presents sheets and confirmations in one window's modal layer.</summary>
/// <gotcha>Every call may come from any thread; the dialogs are built and shown on the UI thread.</gotcha>
public sealed class DialogPresenter(ModalLayer layer)
{
    /// <summary>Opens a sheet with <paramref name="request"/>'s content. Completes once the sheet shows.</summary>
    public async ValueTask<ISheet> ShowSheetAsync(SheetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenSheet open = new();
            open.Show(layer, request);
            return (ISheet)open;
        });
    }

    /// <summary>Asks <paramref name="request"/>'s question; true only for an explicit confirm.</summary>
    /// <gotcha>Cancelling <paramref name="cancellationToken"/> closes the confirmation and answers false.</gotcha>
    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            (bool confirmed, _) = await Dispatcher.UIThread.InvokeAsync(() => ConfirmDialog.AskAsync(layer, request.Title,
                request.Message, request.Details, request.Confirmation, request.Destructive, size: request.Size,
                cancellationToken: cancellationToken));
            return confirmed;
        }
        catch (OperationCanceledException) { return false; }
    }

    // One shown sheet. Closing it, by any route, resolves Completion once and drops the caller's controls and delegates.
    private sealed class OpenSheet : ISheet
    {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Sheet? sheet;

        public Task<bool> Completion => this.completion.Task;

        public void Show(ModalLayer layer, SheetRequest request)
        {
            this.sheet = new Sheet(layer, request.Title, request.Content, request.Description, footer: request.Footer,
                size: request.Size)
            {
                CanDismiss = request.CanDismiss,
                Dismissed = () =>
                {
                    this.Finish(false);
                    return Task.CompletedTask;
                },
            };
            this.sheet.Show();
        }

        public void Complete() => Dispatcher.UIThread.Post(() =>
        {
            if (this.sheet is not { } shown) return;
            shown.Answered();
            shown.Close();
            this.Finish(true);
        });

        public void RequestDismiss() => Dispatcher.UIThread.Post(() => this.sheet?.RequestDismiss());

        public async ValueTask DisposeAsync() => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (this.sheet is { } shown)
            {
                shown.Answered();
                shown.Close();
            }
            this.Finish(false);
        });

        private void Finish(bool answered)
        {
            this.sheet = null;
            this.completion.TrySetResult(answered);
        }
    }
}
