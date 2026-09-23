using System.IO;
using System.Text.Json;

namespace WwanMessenger.App.Services;

public sealed class Settings
{
    public string Mode { get; set; } = "sim";          // "sim", "serial", "mbim"
    public string? PortName { get; set; }
    public int SimScenario { get; set; }               // SimScenario enum value
    public string Theme { get; set; } = "paper";       // "paper" | "graphite" | "nord"
}

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WwanMessenger");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public Settings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? new Settings();
        }
        catch { /* corrupt settings — fall back to defaults */ }
        return new Settings();
    }

    public void Save(Settings settings)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException) { /* non-fatal */ }
    }
}
