using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmuDroid.App.Services;
using EmuDroid.App.Views;
using EmuDroid.Core;
using EmuDroid.Core.Host;
using EmuDroid.Core.Sdk;

namespace EmuDroid.App.ViewModels;

public sealed partial class AndroidImageViewModel(SettingsViewModel owner, AndroidVersionOption option, bool installed) : ObservableObject
{
    public AndroidVersionOption Option { get; } = option;
    public string Name => Option.Name;
    public string SizeText => Format.Bytes(Option.DownloadSize);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private bool _isInstalled = installed;
    [ObservableProperty] private string? _activity;

    public string StatusText => IsInstalled ? "Downloaded" : $"Not downloaded ({SizeText})";

    [RelayCommand] private Task DownloadAsync() => owner.DownloadImageAsync(this);
    [RelayCommand] private void Remove() => owner.RemoveImage(this);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _autoUpdate = services.Settings.AutoUpdate;
    }

    public UpdateViewModel Updates => _main.Updates;
    public string VersionText => $"EmuDroid {AppServices.Version}";
    public string DataFolder => _services.Paths.Root;
    public ObservableCollection<AndroidImageViewModel> Images { get; } = [];

    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _checkTitle = "Not checked yet";
    [ObservableProperty] private string _checkMessage = "";
    [ObservableProperty] private bool _canEnableWhpx;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string? _imagesError;

    partial void OnAutoUpdateChanged(bool value)
    {
        _services.Settings.AutoUpdate = value;
        _services.SaveSettings();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await CheckSystemAsync();
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task CheckSystemAsync()
    {
        IsChecking = true;
        try
        {
            var r = await Task.Run(() => VirtualizationChecker.Check(_services.Paths.Root));
            var f = r.Facts;
            CanEnableWhpx = r.Verdict == VirtualizationVerdict.NeedsWhpx;
            CheckTitle = r.Verdict switch
            {
                VirtualizationVerdict.Ready => "Hardware acceleration is on",
                VirtualizationVerdict.NeedsWhpx => "Windows Hypervisor Platform is off",
                VirtualizationVerdict.NeedsRestart => "Restart Windows to finish turning on acceleration",
                _ => "Virtualization is off in BIOS",
            };
            CheckMessage = $"{f.CpuName}\n{f.TotalRamGb:0} GB RAM, {f.FreeDiskGb:0} GB free disk space";
            if (r.Verdict == VirtualizationVerdict.BiosDisabled) CheckMessage += "\n\n" + VirtualizationChecker.BiosGuide(f.CpuName);
            if (r.Warnings.Count > 0) CheckMessage += "\n" + string.Join("\n", r.Warnings);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task EnableWhpxAsync()
    {
        try
        {
            bool restart = await VirtualizationChecker.EnableWhpxAsync();
            await CheckSystemAsync();
            if (restart && DialogWindow.Confirm("Restart now?", "Windows needs a restart to finish. Save your work in other programs first.", "Restart"))
                VirtualizationChecker.RestartWindows();
        }
        catch (OperationCanceledException)
        {
            _main.ShowToast("Windows permission was not granted, so nothing was changed.", isError: true);
        }
        catch (Exception e)
        {
            Log.Error("Enabling WHPX failed", e);
            _main.ShowToast(Format.Error(e), isError: true);
        }
    }

    private async Task LoadImagesAsync()
    {
        ImagesError = null;
        try
        {
            var catalog = await _services.GetCatalogAsync();
            Images.Clear();
            foreach (var v in catalog.AndroidVersions)
                Images.Add(new AndroidImageViewModel(this, v, _services.IsImageInstalled(v.Package.Path)));
        }
        catch (Exception e)
        {
            ImagesError = Format.Error(e);
        }
    }

    public async Task DownloadImageAsync(AndroidImageViewModel image)
    {
        var progress = new Progress<InstallProgress>(p => image.Activity = p.Download is { } d ? $"{d.Fraction:P0}" : "Installing…");
        try
        {
            await _services.EnsureInstalledAsync(image.Option.Package, progress, CancellationToken.None);
            image.IsInstalled = true;
        }
        catch (Exception e)
        {
            Log.Error("Image download failed", e);
            _main.ShowToast(Format.Error(e), isError: true);
        }
        finally
        {
            image.Activity = null;
        }
    }

    public void RemoveImage(AndroidImageViewModel image)
    {
        var users = _services.Avds.List().Where(c => c.SystemImage == image.Option.Package.Path).ToList();
        if (users.Count > 0)
        {
            DialogWindow.Info($"{image.Name} is in use",
                $"Delete these instances first: {string.Join(", ", users.Select(u => u.DisplayName))}.");
            return;
        }
        if (!DialogWindow.Confirm($"Remove {image.Name}?", $"Frees about {image.SizeText}. You can download it again any time.", "Remove", danger: true))
            return;
        _services.Installer.Uninstall(image.Option.Package.Path);
        image.IsInstalled = false;
    }

    [RelayCommand]
    private void OpenLogs() => Process.Start(new ProcessStartInfo(_services.Paths.Logs) { UseShellExecute = true });

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo(_services.Paths.Root) { UseShellExecute = true });

    [RelayCommand]
    private void OpenRepository() =>
        Process.Start(new ProcessStartInfo("https://github.com/danilomitic/EmuDroid-Android-Emulator") { UseShellExecute = true });
}
