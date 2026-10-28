using Avalonia.Controls;
using Darkspyre.Agent.Common.UI.Composition;

namespace Darkspyre.Agent.Common.UI.Transcript;

/// <summary>Places conversation text in the shared transcript typography section.</summary>
public static class ConversationText
{
    /// <summary>The typography section of conversation text.</summary>
    public const string Section = "transcript";

    /// <summary>Places a control in the conversation text section and returns it.</summary>
    public static T In<T>(T control) where T : Control
    {
        AppearanceSection.SetName(control, Section);
        return control;
    }
}
