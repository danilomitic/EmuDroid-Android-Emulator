using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmuDroid.Keymap;

public enum ControlType { Tap, Joystick, Aim, Fire }

/// <summary>One on-screen control. Positions are fractions (0..1) of the Android screen, so profiles work at any window size.</summary>
public sealed class KeyControl
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ControlType Type { get; set; }
    /// <summary>Key name as in System.Windows.Input.Key (e.g. "Space", "E"). Joystick uses Up/Left/Down/Right keys instead.</summary>
    public string Key { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Joystick/aim radius as a fraction of the screen width.</summary>
    public double Radius { get; set; } = 0.08;
    public string Up { get; set; } = "W";
    public string Left { get; set; } = "A";
    public string Down { get; set; } = "S";
    public string Right { get; set; } = "D";

    public string Label => Type switch
    {
        ControlType.Joystick => $"{Up}{Left}{Down}{Right}",
        ControlType.Aim => "Aim",
        ControlType.Fire => "Fire",
        _ => Key,
    };
}

public sealed class KeymapProfile
{
    public string PackageName { get; set; } = DefaultName;
    /// <summary>Key that toggles aim mode (mouse-look). Mouse right button by default.</summary>
    public string AimToggle { get; set; } = "MouseRight";
    public double AimSensitivity { get; set; } = 1.0;
    public List<KeyControl> Controls { get; set; } = [];

    public const string DefaultName = "default";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileFor(string folder, string packageName) =>
        Path.Combine(folder, Sanitize(packageName) + ".json");

    /// <summary>Loads the profile for an app, falling back to the default profile, then to an empty one.</summary>
    public static KeymapProfile Load(string folder, string? packageName)
    {
        foreach (var name in new[] { packageName, DefaultName })
        {
            if (string.IsNullOrEmpty(name)) continue;
            string file = FileFor(folder, name);
            if (!File.Exists(file)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<KeymapProfile>(File.ReadAllText(file), Json);
                if (p is not null)
                {
                    p.PackageName = name;
                    return p;
                }
            }
            catch (JsonException) { }
        }
        return new KeymapProfile { PackageName = packageName ?? DefaultName };
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FileFor(folder, PackageName), JsonSerializer.Serialize(this, Json));
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
}
