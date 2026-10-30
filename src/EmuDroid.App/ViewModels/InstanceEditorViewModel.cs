using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmuDroid.Core.Avd;
using EmuDroid.Core.Sdk;

namespace EmuDroid.App.ViewModels;

/// <summary>Create / edit instance dialog.</summary>
public sealed partial class InstanceEditorViewModel : ObservableObject
{
    public InstanceEditorViewModel(IEnumerable<AndroidVersionOption> versions, InstanceConfig? existing)
    {
        IsNew = existing is null;
        foreach (var v in versions) Versions.Add(v);
        MaxCores = InstanceConfig.MaxCores;

        if (existing is null)
        {
            _name = "My Android";
            _selectedVersion = Versions.FirstOrDefault(v => v.ApiLevel == 34) ?? Versions.FirstOrDefault();
            _cores = Math.Min(4, MaxCores);
        }
        else
        {
            _name = existing.DisplayName;
            _selectedVersion = Versions.FirstOrDefault(v => v.Package.Path == existing.SystemImage);
            _ramGb = existing.RamMb / 1024;
            _cores = existing.CpuCores;
            _gpu = existing.Gpu;
            _screen = ScreenPreset.All.FirstOrDefault(s => s.Width == existing.Width && s.Height == existing.Height) ?? ScreenPreset.Phone;
        }
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "New instance" : "Instance settings";
    public string SaveText => IsNew ? "Create instance" : "Save changes";
    public ObservableCollection<AndroidVersionOption> Versions { get; } = [];
    public IReadOnlyList<ScreenPreset> Screens => ScreenPreset.All;
    public IReadOnlyList<GpuMode> GpuModes { get; } = Enum.GetValues<GpuMode>();
    public int MaxCores { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private AndroidVersionOption? _selectedVersion;
    [ObservableProperty] private int _ramGb = 4;
    [ObservableProperty] private int _cores;
    [ObservableProperty] private GpuMode _gpu = GpuMode.Auto;
    [ObservableProperty] private ScreenPreset _screen = ScreenPreset.Phone;

    /// <summary>Changing Android version of an existing instance would break its data, so it is locked.</summary>
    public bool CanChangeVersion => IsNew;

    public InstanceConfig Apply(InstanceConfig cfg) => cfg with
    {
        DisplayName = string.IsNullOrWhiteSpace(Name) ? cfg.DisplayName : Name.Trim(),
        RamMb = RamGb * 1024,
        CpuCores = Cores,
        Gpu = Gpu,
        Width = Screen.Width,
        Height = Screen.Height,
        Dpi = Screen.Dpi,
    };
}
