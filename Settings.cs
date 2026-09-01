using System.Text.Json;

namespace Yako.Screenshot;

internal sealed class Settings
{
    public string? LastSaveDirectory { get; set; }

    /// <summary>Drives the one-time "it is running, here is the hotkey" balloon.</summary>
    public bool FirstRunDone { get; set; }

    private static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yako");

    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    /// <summary>Never throws - a corrupt or unreadable file simply means defaults.</summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // fall through to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Losing a preference is not worth interrupting a capture over.
        }
    }
}
