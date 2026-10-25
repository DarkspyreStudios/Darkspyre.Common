namespace Darkspyre.Agent.Common.UI.Dialogs;

/// <summary>
/// The width of a sheet or confirmation. A dialog opens at exactly its size's width and narrows only when the window
/// leaves less room than that.
/// </summary>
public enum DialogSize
{
    /// <summary>512 px, for confirmations and very short prompts. The confirmation default.</summary>
    Narrow,

    /// <summary>720 px, for forms and read-only details. The sheet default.</summary>
    Medium,

    /// <summary>960 px, for editors with lists or tables, logs and documents.</summary>
    Wide,
}

/// <summary>The style classes that give a dialog's frame its <see cref="DialogSize"/>.</summary>
public static class DialogSizes
{
    /// <summary>
    /// The style class that sets <paramref name="size"/>'s width on a <c>Border.sheet</c> or <c>Border.alert-dialog</c>
    /// frame: <c>dialog-narrow</c>, <c>dialog-medium</c> or <c>dialog-wide</c>.
    /// </summary>
    /// <gotcha>A dialog that builds its own <c>Border.alert-dialog</c> frame, instead of using <see cref="Sheet"/> or
    /// <see cref="ConfirmDialog"/>, adds this class to that frame itself. Without one of these classes the frame takes its
    /// content's width.</gotcha>
    public static string StyleClass(this DialogSize size) => size switch
    {
        DialogSize.Narrow => "dialog-narrow",
        DialogSize.Medium => "dialog-medium",
        DialogSize.Wide => "dialog-wide",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Not a dialog size."),
    };
}
