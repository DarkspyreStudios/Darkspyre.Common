namespace Darkspyre.Common.Tests.Support;

/// <summary>
/// Reads the Core <c>default-temp-location</c> setting from <c>~/.agent-ds/settings.json</c>, the one place a test
/// scratch root's parent folder comes from. Shared by every Darkspyre.Common test project that owns its own scratch
/// root, so the setting is read the same way everywhere (L9) instead of each project parsing the file itself.
/// </summary>
internal static class TestDefaultTempLocation
{
    /// <summary>The stored setting value, or null when the settings file or the value is missing or unreadable.</summary>
    public static string? Read()
    {
        // The signed test host reports an empty user profile folder on macOS while HOME names the home folder.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
            ? profile : Environment.GetEnvironmentVariable("HOME") ?? "";
        if (home.Length == 0) return null;
        string file = Path.Combine(home, ".agent-ds", "settings.json");
        try
        {
            using System.Text.Json.JsonDocument settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            return settings.RootElement.TryGetProperty("agent-ds.core", out System.Text.Json.JsonElement core)
                && core.TryGetProperty("default-temp-location", out System.Text.Json.JsonElement value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
