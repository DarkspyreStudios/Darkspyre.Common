using Avalonia.Controls;
using Avalonia.Layout;
using Darkspyre.Agent.Common.UI.Composition;
using Darkspyre.Agent.Common.UI.Errors;

namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// The dialog every sheet in the application uses: a title, an optional description, content and a footer, and one
/// dismissal report when it closes unanswered. Its contents form a composition target, so plugins can add to it.
/// </summary>
/// <remarks>
/// Every way out arrives at <see cref="RequestDismiss"/>: the close button, Escape and the click outside. A sheet that
/// answers with a value calls <see cref="Answered"/>, and the close that follows the answer is then not reported as a
/// dismissal; without that, a save is immediately followed by a cancel.
/// </remarks>
public sealed class Sheet : IModal
{
    private readonly ModalLayer layer;
    private readonly Border view;
    private bool answered;
    private bool shown;

    /// <summary>
    /// Creates a sheet titled <paramref name="title"/> in <paramref name="layer"/>. Call <see cref="Show"/> to open it.
    /// <paramref name="kind"/> holds the view's style classes, separated by spaces, such as "sheet provider-editor".
    /// <paramref name="size"/> sets its width; it stays inside the window less the margins.
    /// </summary>
    public Sheet(ModalLayer layer, string title, Control? content = null, string? description = null, Control? leading = null,
        Control? footer = null, string kind = "sheet", string targetName = "dialog", SurfaceScopeContext? scope = null,
        DialogSize size = DialogSize.Medium)
    {
        this.layer = layer;
        StackPanel heading = new() { Spacing = 4 };
        heading.Classes.Add("dialog-header");
        TextBlock titleText = new() { Text = title };
        titleText.Classes.Add("dialog-title");
        heading.Children.Add(titleText);
        if (description is { Length: > 0 })
        {
            TextBlock describe = new() { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            describe.Classes.Add("dialog-description");
            heading.Children.Add(describe);
        }
        Control header = heading;
        if (leading is not null)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Classes.Add("dialog-header-row");
            row.Children.Add(leading);
            row.Children.Add(heading);
            header = row;
        }
        Button close = new() { Content = "✕" };
        close.Classes.Add("dialog-close");
        close.Click += (_, _) => this.RequestDismiss();
        Avalonia.Automation.AutomationProperties.SetName(close, "Close");
        DockPanel top = new();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(header);
        DockPanel layout = new();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        if (footer is not null)
        {
            footer.Classes.Add("dialog-footer");
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
        }
        layout.Children.Add(new ScrollViewer
        {
            Content = new UiRegion("Sheet " + title, () => content ?? new Panel()),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        CompositionTarget target = new() { TargetName = targetName, Scope = scope ?? new(), Content = layout };
        target.Classes.Add("dialog-composition-content");
        this.view = new Border { Child = target, Focusable = true };
        // Each class is added on its own. One class named "sheet provider-editor" matches no Border.sheet style, and the
        // dialog then has no background, width limit or padding.
        this.view.Classes.AddRange(kind.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        this.view.Classes.Add(size.StyleClass());
    }

    /// <inheritdoc />
    public Control View => this.view;

    /// <summary>Runs once, when the sheet closes without being answered.</summary>
    public Func<Task>? Dismissed { get; init; }

    /// <summary>Optional owner guard for unsaved edits; false keeps the sheet open.</summary>
    public Func<bool>? CanDismiss { get; init; }

    /// <summary>Opens the sheet.</summary>
    public void Show()
    {
        if (this.shown) return;
        this.shown = true;
        this.layer.Show(this);
    }

    /// <summary>Told by the owner that it has answered, so the close that follows is not reported as a dismissal too.</summary>
    public void Answered() => this.answered = true;

    /// <summary>Closes the sheet; reported as a dismissal unless it was answered.</summary>
    public void Close()
    {
        this.layer.Close(this);
        this.shown = false;
    }

    /// <inheritdoc />
    public void RequestDismiss()
    {
        if (this.answered || !(this.CanDismiss?.Invoke() ?? true)) return;
        this.answered = true;
        this.Close();
        if (this.Dismissed is { } dismissed) _ = dismissed();
    }
}
