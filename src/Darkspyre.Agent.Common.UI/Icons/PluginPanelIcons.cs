
namespace Darkspyre.Agent.Common.UI.Icons;

/// <summary>Maps a plugin panel's icon token to Darkspyre artwork, so shared plugin chrome shows only application-owned icons.</summary>
public static class PluginPanelIcons
{
    /// <summary>Returns the icon for a known token, or the panel icon for an unknown or missing one.</summary>
    public static IconSource Resolve(string? token) => token switch
    {
        "document" => AppIcons.Document,
        "folder" => AppIcons.Folder,
        "branch" => AppIcons.Branch,
        "diff" => AppIcons.Diff,
        "activity" => AppIcons.Activity,
        "terminal" => AppIcons.Terminal,
        "hammer" => AppIcons.Hammer,
        "chat" => AppIcons.Chat,
        "server" => AppIcons.Server,
        "globe" => AppIcons.Globe,
        _ => AppIcons.Panel,
    };
}
