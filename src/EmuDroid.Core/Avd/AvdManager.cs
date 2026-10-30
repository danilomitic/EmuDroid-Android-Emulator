using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmuDroid.Core.Avd;

/// <summary>
/// Creates and edits AVDs by writing the same files avdmanager would (no Java needed).
/// AVDs live in %LocalAppData%\EmuDroid\avd, passed to the emulator as ANDROID_AVD_HOME.
/// </summary>
public sealed partial class AvdManager(AppPaths paths)
{
    private const string MetadataFile = "emudroid.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string AvdDirectory(string id) => Path.Combine(paths.Avd, id + ".avd");
    private string IniFile(string id) => Path.Combine(paths.Avd, id + ".ini");

    public IReadOnlyList<InstanceConfig> List()
    {
        if (!Directory.Exists(paths.Avd)) return [];
        var result = new List<InstanceConfig>();
        foreach (var dir in Directory.GetDirectories(paths.Avd, "*.avd"))
        {
            string meta = Path.Combine(dir, MetadataFile);
            if (!File.Exists(meta)) continue;
            try
            {
                if (JsonSerializer.Deserialize<InstanceConfig>(File.ReadAllText(meta), Json) is { } cfg)
                    result.Add(cfg);
            }
            catch (JsonException) { /* Skip broken instances instead of crashing the whole list. */ }
        }
        return result.OrderBy(c => c.CreatedAt).ToList();
    }

    public InstanceConfig Create(string displayName, int apiLevel, string systemImage, Func<InstanceConfig, InstanceConfig>? customize = null)
    {
        var cfg = new InstanceConfig
        {
            Id = UniqueId(displayName),
            DisplayName = displayName.Trim(),
            ApiLevel = apiLevel,
            SystemImage = systemImage,
            CpuCores = Math.Min(4, InstanceConfig.MaxCores),
        };
        if (customize is not null) cfg = customize(cfg) with { Id = cfg.Id };
        Directory.CreateDirectory(AvdDirectory(cfg.Id));
        Save(cfg);
        return cfg;
    }

    /// <summary>Writes config.ini, the top-level .ini and EmuDroid's own metadata.</summary>
    public void Save(InstanceConfig cfg)
    {
        string dir = AvdDirectory(cfg.Id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(IniFile(cfg.Id), BuildTopIni(cfg, dir), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "config.ini"), BuildConfigIni(cfg), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, MetadataFile), JsonSerializer.Serialize(cfg, Json));
    }

    public InstanceConfig Rename(InstanceConfig cfg, string newName)
    {
        var renamed = cfg with { DisplayName = newName.Trim() };
        Save(renamed);
        return renamed;
    }

    /// <summary>Copies an instance with all its data (apps, accounts). The instance must be stopped.</summary>
    public InstanceConfig Clone(InstanceConfig source, string newName)
    {
        var clone = source with { Id = UniqueId(newName), DisplayName = newName.Trim(), CreatedAt = DateTimeOffset.Now };
        CopyDirectory(AvdDirectory(source.Id), AvdDirectory(clone.Id));
        foreach (var lockFile in Directory.EnumerateFiles(AvdDirectory(clone.Id), "*.lock", SearchOption.AllDirectories))
            File.Delete(lockFile);
        foreach (var lockDir in Directory.EnumerateDirectories(AvdDirectory(clone.Id), "*.lock", SearchOption.AllDirectories).ToList())
            Directory.Delete(lockDir, true);
        Save(clone);
        return clone;
    }

    /// <summary>Factory reset: deletes user data and snapshots. The emulator recreates them from the system image.</summary>
    public void WipeData(InstanceConfig cfg)
    {
        string dir = AvdDirectory(cfg.Id);
        foreach (var pattern in new[] { "userdata-qemu.img*", "cache.img*", "encryptionkey.img*", "*.qcow2", "sdcard.img*" })
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
                File.Delete(f);
        string snapshots = Path.Combine(dir, "snapshots");
        if (Directory.Exists(snapshots)) Directory.Delete(snapshots, true);
    }

    public void Delete(InstanceConfig cfg)
    {
        string dir = AvdDirectory(cfg.Id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (File.Exists(IniFile(cfg.Id))) File.Delete(IniFile(cfg.Id));
    }

    public static string BuildTopIni(InstanceConfig cfg, string avdDir) => $"""
        avd.ini.encoding=UTF-8
        path={avdDir}
        target=android-{cfg.ApiLevel}

        """;

    public static string BuildConfigIni(InstanceConfig cfg)
    {
        // "system-images;android-34;google_apis_playstore;x86_64" → "system-images/android-34/google_apis_playstore/x86_64/"
        // (relative to ANDROID_SDK_ROOT, like avdmanager writes it).
        string sysDir = cfg.SystemImage.Replace(';', '/') + "/";
        var ic = CultureInfo.InvariantCulture;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AvdId"] = cfg.Id,
            ["PlayStore.enabled"] = "true",
            ["abi.type"] = "x86_64",
            ["avd.ini.displayname"] = cfg.DisplayName,
            ["avd.ini.encoding"] = "UTF-8",
            ["disk.dataPartition.size"] = "8G",
            ["fastboot.forceColdBoot"] = "no",
            ["hw.accelerometer"] = "yes",
            ["hw.audioInput"] = "yes",
            ["hw.battery"] = "yes",
            ["hw.camera.back"] = "emulated",
            ["hw.camera.front"] = "emulated",
            ["hw.cpu.arch"] = "x86_64",
            ["hw.cpu.ncore"] = cfg.CpuCores.ToString(ic),
            ["hw.dPad"] = "no",
            ["hw.gps"] = "yes",
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = GpuValue(cfg.Gpu),
            ["hw.keyboard"] = "yes",
            ["hw.lcd.density"] = cfg.Dpi.ToString(ic),
            ["hw.lcd.height"] = cfg.Height.ToString(ic),
            ["hw.lcd.width"] = cfg.Width.ToString(ic),
            ["hw.mainKeys"] = "no",
            ["hw.ramSize"] = cfg.RamMb.ToString(ic),
            ["hw.sdCard"] = "yes",
            ["hw.sensors.orientation"] = "yes",
            ["hw.sensors.proximity"] = "yes",
            ["hw.trackBall"] = "no",
            ["image.sysdir.1"] = sysDir,
            ["sdcard.size"] = "512M",
            ["showDeviceFrame"] = "no",
            ["skin.dynamic"] = "yes",
            ["tag.display"] = "Google Play",
            ["tag.id"] = "google_apis_playstore",
            ["vm.heapSize"] = "256",
        };
        var sb = new StringBuilder();
        foreach (var (k, v) in values) sb.Append(k).Append('=').Append(v).Append('\n');
        return sb.ToString();
    }

    public static string GpuValue(GpuMode mode) => mode switch
    {
        GpuMode.Host => "host",
        GpuMode.Software => "swiftshader_indirect",
        _ => "auto",
    };

    /// <summary>Turns a display name into a folder-safe AVD id, e.g. "My Phone #2" → "My_Phone_2".</summary>
    public static string SanitizeId(string name)
    {
        string id = UnsafeChars().Replace(name.Trim(), "_").Trim('_', '.', '-');
        id = RepeatedUnderscores().Replace(id, "_");
        if (id.Length == 0) id = "Instance";
        return id.Length > 40 ? id[..40] : id;
    }

    private string UniqueId(string displayName)
    {
        string baseId = SanitizeId(displayName);
        string id = baseId;
        for (int i = 2; Directory.Exists(AvdDirectory(id)) || File.Exists(IniFile(id)); i++)
            id = $"{baseId}_{i}";
        return id;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex("_{2,}")]
    private static partial Regex RepeatedUnderscores();
}
