using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdViewer;

/// <summary>User preferences persisted between runs.</summary>
internal sealed class Settings
{
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
    [JsonPropertyName("tocVisible")] public bool TocVisible { get; set; } = true;
    [JsonPropertyName("singleWindow")] public bool SingleWindow { get; set; } = true;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("x")] public int X { get; set; } = int.MinValue;
    [JsonPropertyName("y")] public int Y { get; set; } = int.MinValue;
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupted or unreadable settings must never block startup.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Losing preferences is not worth crashing over.
        }
    }
}

internal static class AppPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdViewer");

    public static string BrowserFolder { get; } = Path.Combine(DataFolder, "browser");
}
