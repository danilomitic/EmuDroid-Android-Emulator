using System.Text.Json;

namespace EmuDroid.Core;

public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public bool LicenseAccepted { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public string OverlayHotkey { get; set; } = "F12";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new AppSettings();
    }

    public void Save(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        string tmp = paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, paths.SettingsFile, overwrite: true);
    }
}
