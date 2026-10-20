using Avalonia;
using Avalonia.Controls;

namespace Darkspyre.Agent.Common.UI.Composition;

/// <summary>
/// Names the typography section a control and everything inside it belongs to. The user's font choices for that section,
/// in Settings, Appearance, apply there. The application's own sections are <c>navigation</c>, <c>transcript</c>,
/// <c>tools</c> and <c>settings</c>; a plugin names its own, such as <c>my-plugin</c>.
/// </summary>
/// <gotcha>
/// The section's fonts reach a control through the text entry resources <c>font-{entry}</c> and
/// <c>font-size-{entry}</c>, such as <c>font-body</c> and <c>font-size-body</c>, <c>font-secondary</c>,
/// <c>font-code</c> or <c>font-heading-1</c> to <c>font-heading-6</c>. A plugin control that sets its own
/// <c>FontFamily</c> or <c>FontSize</c> to a value instead of one of these resources does not follow them.
/// </gotcha>
public static class AppearanceSection
{
    /// <summary>Defines the attached section name.</summary>
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Name", typeof(AppearanceSection));

    static AppearanceSection() =>
        NameProperty.Changed.AddClassHandler<Control>((control, change) => Registry?.SectionChanged(control,
            change.GetNewValue<string?>()));

    /// <summary>The section <paramref name="control"/> belongs to, or null.</summary>
    public static string? GetName(Control control) => control.GetValue(NameProperty);

    /// <summary>Places <paramref name="control"/> in <paramref name="section"/>; null removes it from its section.</summary>
    public static void SetName(Control control, string? section) => control.SetValue(NameProperty, section);

    /// <summary>The application's owner of section fonts; installed once at startup. With none, a section changes nothing.</summary>
    public static IAppearanceSectionRegistry? Registry { get; set; }
}

/// <summary>The application's owner of section fonts, told when a control joins or leaves a section.</summary>
public interface IAppearanceSectionRegistry
{
    /// <summary>Moves <paramref name="control"/> into section <paramref name="section"/>; null takes it out of its section.</summary>
    void SectionChanged(Control control, string? section);
}
