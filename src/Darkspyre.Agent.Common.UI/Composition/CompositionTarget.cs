using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Darkspyre.Agent.Common.UI.Composition;

/// <summary>
/// A composable container shared by an application's own views and its plugins' views. Surfaces attached to it render
/// before or after it, at the start or end of its content, or anchored to it. <c>CommonTheme</c> lays out its content
/// and the four inline slots, and the application's <see cref="Presenter"/> fills each slot.
/// </summary>
public sealed class CompositionTarget : TemplatedControl
{
    /// <summary>Defines the <see cref="TargetName"/> property.</summary>
    public static readonly StyledProperty<string?> TargetNameProperty =
        AvaloniaProperty.Register<CompositionTarget, string?>(nameof(TargetName));
    /// <summary>Defines the <see cref="Tags"/> property.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>> TagsProperty =
        AvaloniaProperty.Register<CompositionTarget, IReadOnlyList<string>>(nameof(Tags), []);
    /// <summary>Defines the <see cref="Scope"/> property.</summary>
    public static readonly StyledProperty<SurfaceScopeContext> ScopeProperty =
        AvaloniaProperty.Register<CompositionTarget, SurfaceScopeContext>(nameof(Scope), new SurfaceScopeContext());
    /// <summary>Defines the <see cref="Content"/> property.</summary>
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<CompositionTarget, object?>(nameof(Content));
    /// <summary>Defines the <see cref="Before"/> property.</summary>
    public static readonly StyledProperty<object?> BeforeProperty =
        AvaloniaProperty.Register<CompositionTarget, object?>(nameof(Before));
    /// <summary>Defines the <see cref="After"/> property.</summary>
    public static readonly StyledProperty<object?> AfterProperty =
        AvaloniaProperty.Register<CompositionTarget, object?>(nameof(After));
    /// <summary>Defines the <see cref="InsideStart"/> property.</summary>
    public static readonly StyledProperty<object?> InsideStartProperty =
        AvaloniaProperty.Register<CompositionTarget, object?>(nameof(InsideStart));
    /// <summary>Defines the <see cref="InsideEnd"/> property.</summary>
    public static readonly StyledProperty<object?> InsideEndProperty =
        AvaloniaProperty.Register<CompositionTarget, object?>(nameof(InsideEnd));

    /// <summary>The semantic name that target queries match, or null.</summary>
    public string? TargetName
    {
        get => this.GetValue(TargetNameProperty);
        set => this.SetValue(TargetNameProperty, value);
    }

    /// <summary>The semantic tags that target queries match.</summary>
    public IReadOnlyList<string> Tags
    {
        get => this.GetValue(TagsProperty);
        set => this.SetValue(TagsProperty, value);
    }

    /// <summary>The project and session the target belongs to.</summary>
    public SurfaceScopeContext Scope
    {
        get => this.GetValue(ScopeProperty);
        set => this.SetValue(ScopeProperty, value);
    }

    /// <summary>The container's own content, shown between the inside-start and inside-end attachments.</summary>
    public object? Content
    {
        get => this.GetValue(ContentProperty);
        set => this.SetValue(ContentProperty, value);
    }

    /// <summary>Content shown before the container, ahead of attached <c>Before</c> surfaces.</summary>
    public object? Before
    {
        get => this.GetValue(BeforeProperty);
        set => this.SetValue(BeforeProperty, value);
    }

    /// <summary>Content shown after the container, following attached <c>After</c> surfaces.</summary>
    public object? After
    {
        get => this.GetValue(AfterProperty);
        set => this.SetValue(AfterProperty, value);
    }

    /// <summary>Content shown at the start of the container, ahead of attached <c>InsideStart</c> surfaces.</summary>
    public object? InsideStart
    {
        get => this.GetValue(InsideStartProperty);
        set => this.SetValue(InsideStartProperty, value);
    }

    /// <summary>Content shown at the end of the container, following attached <c>InsideEnd</c> surfaces.</summary>
    public object? InsideEnd
    {
        get => this.GetValue(InsideEndProperty);
        set => this.SetValue(InsideEndProperty, value);
    }

    /// <summary>The application's presenter of composition targets, told when a target enters and leaves a window.</summary>
    /// <gotcha>The application sets it once, before any view is shown. With none set, a target shows its own content and
    /// empty slots, and nothing can attach to it.</gotcha>
    public static ICompositionPresenter? Presenter { get; set; }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Presenter?.Add(this);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Presenter?.Remove(this);
        base.OnDetachedFromVisualTree(e);
    }
}

/// <summary>One of a composition target's four inline slots.</summary>
public enum CompositionSlot
{
    /// <summary>Inline, before the target.</summary>
    Before,
    /// <summary>Inline, after the target.</summary>
    After,
    /// <summary>Inline, at the start of the target's content.</summary>
    InsideStart,
    /// <summary>Inline, at the end of the target's content.</summary>
    InsideEnd,
}

/// <summary>The application's presenter of composition targets: it tracks the targets in a window and fills their slots.</summary>
public interface ICompositionPresenter
{
    /// <summary>Called when <paramref name="target"/> enters a window.</summary>
    void Add(CompositionTarget target);

    /// <summary>Called when <paramref name="target"/> leaves its window.</summary>
    void Remove(CompositionTarget target);

    /// <summary>Creates the control that shows the surfaces attached to <paramref name="target"/> in <paramref name="slot"/>.</summary>
    Control CreateSlot(CompositionTarget target, CompositionSlot slot);
}

/// <summary>The project and session a surface instance belongs to.</summary>
/// <param name="ProjectId">The project, or null for none. It must not be empty when given.</param>
/// <param name="SessionId">The session, or null for none. It must not be empty when given.</param>
public sealed record SurfaceScopeContext(Guid? ProjectId = null, Guid? SessionId = null);
