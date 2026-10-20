using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Darkspyre.Agent.Common.UI.Composition;

namespace Darkspyre.Agent.Common.UI.Themes;

/// <summary>
/// The shared controls' look: the common control styles, the colour picker's templates and the
/// <see cref="CompositionTarget"/> template. An application adds it to its styles before its own.
/// </summary>
/// <gotcha>The styles read the application's theme resources through <c>DynamicResource</c>; README.md lists the keys.</gotcha>
public sealed class CommonTheme : Styles
{
    /// <summary>Creates the shared styles.</summary>
    public CommonTheme()
    {
        // The colour view's templates, for the colour picker.
        this.Add(new StyleInclude(new Uri("avares://Darkspyre.Agent.Common.UI"))
        {
            Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml"),
        });
        this.Add(new ControlStyles());
        this.Resources[typeof(CompositionTarget)] = CompositionTargetTheme();
    }

    /// <summary>
    /// A composition target's slots in order: its Before content and Before slot above it, then the container with its
    /// InsideStart content and slot, its own content, and its InsideEnd slot and content, then its After slot and After
    /// content below it. <see cref="CompositionTarget.Presenter"/> fills each slot; with none, a slot is empty.
    /// </summary>
    private static ControlTheme CompositionTargetTheme() => new(typeof(CompositionTarget))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<CompositionTarget>((target, _) =>
            {
                DockPanel outer = new() { LastChildFill = true };
                Add(outer, Presenter(nameof(CompositionTarget.Before)), Dock.Top);
                Add(outer, Slot(target, CompositionSlot.Before), Dock.Top);
                Add(outer, Presenter(nameof(CompositionTarget.After)), Dock.Bottom);
                Add(outer, Slot(target, CompositionSlot.After), Dock.Bottom);
                DockPanel inner = new() { LastChildFill = true };
                inner.Classes.Add("composition-target");
                Add(inner, Presenter(nameof(CompositionTarget.InsideStart)), Dock.Top);
                Add(inner, Slot(target, CompositionSlot.InsideStart), Dock.Top);
                Add(inner, Presenter(nameof(CompositionTarget.InsideEnd)), Dock.Bottom);
                Add(inner, Slot(target, CompositionSlot.InsideEnd), Dock.Bottom);
                inner.Children.Add(Presenter(nameof(CompositionTarget.Content)));
                outer.Children.Add(inner);
                return outer;
            })),
        },
    };

    private static Control Slot(CompositionTarget target, CompositionSlot slot) =>
        CompositionTarget.Presenter?.CreateSlot(target, slot) ?? new Panel();

    private static void Add(DockPanel panel, Control child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        panel.Children.Add(child);
    }

    private static ContentPresenter Presenter(string property)
    {
        ContentPresenter presenter = new();
        presenter.Bind(ContentPresenter.ContentProperty, new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        return presenter;
    }
}
