using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Darkspyre.Agent.Common.UI.Controls;

/// <summary>Shows content built from <see cref="ContentControl.ContentTemplate"/> and builds it again from new controls whenever <see cref="Key"/> changes.</summary>
public sealed class Remount : ContentControl
{
    /// <summary>Defines the <see cref="Key"/> property.</summary>
    public static readonly StyledProperty<object?> KeyProperty = AvaloniaProperty.Register<Remount, object?>(nameof(Key));

    static Remount() => KeyProperty.Changed.AddClassHandler<Remount>((remount, _) => remount.Rebuild());

    /// <summary>The key. Changing it rebuilds the content.</summary>
    public object? Key
    {
        get => this.GetValue(KeyProperty);
        set => this.SetValue(KeyProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentTemplateProperty)
        {
            this.Rebuild();
        }
    }

    private void Rebuild() => this.Content = this.ContentTemplate is IDataTemplate template ? template.Build(this.Key) : null;
}
