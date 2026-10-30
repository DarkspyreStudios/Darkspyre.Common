using System.Text.Json;

namespace Darkspyre.Common.TestSupport;

/// <summary>
/// Reads the <c>default-temp-location</c> user setting, the same folder <c>StorageLocations</c> reports to every
/// agent run as the temporary-files location. Shared by every scratch-root fixture in this repository's test
/// projects (compiled into each by a <c>Compile Include</c> link, not copied), so a moved temporary-files setting
/// takes every fixture with it, the way it already takes every agent run.
/// </summary>
internal static class TestDefaultTempLocation
{
    private const string SettingsFolderName = ".agent-ds";
    private const string SettingsFileName = "settings.json";
    private const string OwnerKey = "agent-ds.core";
    private const string SettingKey = "default-temp-location";

    /// <summary>
    /// The folder the setting names, or null when the settings file does not exist, cannot be parsed, or carries no
    /// value for the setting. This reads the live file; it is not cached.
    /// </summary>
    internal static string? Read()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SettingsFolderName, SettingsFileName);
        if (!File.Exists(path)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty(OwnerKey, out JsonElement owner) || owner.ValueKind != JsonValueKind.Object) return null;
            if (!owner.TryGetProperty(SettingKey, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
            string text = (value.GetString() ?? string.Empty).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
