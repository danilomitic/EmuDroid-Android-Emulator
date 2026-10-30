using System.Text.Json.Serialization;

namespace EmuDroid.Core.Avd;

public enum GpuMode { Auto, Host, Software }

public sealed record ScreenPreset(string Name, int Width, int Height, int Dpi)
{
    public static readonly ScreenPreset Phone = new("Phone (1080 × 2400)", 1080, 2400, 420);
    public static readonly ScreenPreset Tablet = new("Tablet (1600 × 2560)", 1600, 2560, 320);
    public static readonly ScreenPreset Landscape = new("Landscape 1080p (1920 × 1080)", 1920, 1080, 280);
    public static readonly IReadOnlyList<ScreenPreset> All = [Phone, Tablet, Landscape];

    public override string ToString() => Name;
}

/// <summary>One EmuDroid instance (one Android Virtual Device).</summary>
public sealed record InstanceConfig
{
    /// <summary>Folder-safe id, never changes after creation.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required int ApiLevel { get; init; }
    /// <summary>SDK package path of the system image, e.g. "system-images;android-34;google_apis_playstore;x86_64".</summary>
    public required string SystemImage { get; init; }
    public int RamMb { get; init; } = 4096;
    public int CpuCores { get; init; } = 4;
    public int Width { get; init; } = ScreenPreset.Phone.Width;
    public int Height { get; init; } = ScreenPreset.Phone.Height;
    public int Dpi { get; init; } = ScreenPreset.Phone.Dpi;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GpuMode Gpu { get; init; } = GpuMode.Auto;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string AndroidName => Sdk.RepositoryParser.AndroidName(ApiLevel);

    public static int MaxCores => Math.Max(1, Environment.ProcessorCount);
}
