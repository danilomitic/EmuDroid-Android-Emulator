using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmuDroid.App.Services;
using EmuDroid.App.Views;
using EmuDroid.Core;
using EmuDroid.Core.Adb;
using EmuDroid.Core.Avd;

namespace EmuDroid.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public HomeViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        foreach (var cfg in services.Avds.List()) Instances.Add(new InstanceViewModel(services, this, cfg));
        services.Emulators.StateChanged += OnStateChanged;
    }

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];
    public bool IsEmpty => Instances.Count == 0;

    private void OnStateChanged(object? sender, InstanceStateChangedEventArgs e) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = Instances.FirstOrDefault(i => i.Id == e.InstanceId);
            if (vm is null) return;
            vm.State = e.State;
            if (e.Error is not null) vm.Error = e.Error;
        });

    public void Add(InstanceConfig cfg)
    {
        Instances.Add(new InstanceViewModel(_services, this, cfg));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Remove(InstanceViewModel vm)
    {
        Instances.Remove(vm);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task NewInstanceAsync()
    {
        var versions = await LoadVersionsAsync();
        if (versions is null) return;

        var editor = new InstanceEditorViewModel(versions, existing: null);
        if (InstanceEditorWindow.Show(editor) != true || editor.SelectedVersion is null) return;

        var v = editor.SelectedVersion;
        var cfg = _services.Avds.Create(editor.Name, v.ApiLevel, v.Package.Path, c => editor.Apply(c));
        Add(cfg);

        if (!_services.IsImageInstalled(v.Package.Path) || !_services.CoreComponentsInstalled)
        {
            var card = Instances.Last();
            try
            {
                await card.RunWithProgressAsync("Downloading", p => _services.EnsureInstalledAsync(v.Package, p, CancellationToken.None));
            }
            catch (Exception e)
            {
                Log.Error("Downloading image for new instance failed", e);
                card.Error = Format.Error(e) + " Press Start to try again.";
            }
        }
    }

    public async Task EditInstanceAsync(InstanceViewModel vm)
    {
        var versions = await LoadVersionsAsync();
        if (versions is null) return;
        var editor = new InstanceEditorViewModel(versions, vm.Config);
        if (InstanceEditorWindow.Show(editor) != true) return;
        var updated = editor.Apply(vm.Config);
        _services.Avds.Save(updated);
        vm.Config = updated;
    }

    private async Task<IReadOnlyList<Core.Sdk.AndroidVersionOption>?> LoadVersionsAsync()
    {
        try
        {
            return (await _services.GetCatalogAsync()).AndroidVersions;
        }
        catch (Exception e)
        {
            Log.Error("Loading catalog failed", e);
            DialogWindow.Info("Can't load Android versions", Format.Error(e));
            return null;
        }
    }

    /// <summary>Installs dropped APK files on a running instance (the given one, or the only running one).</summary>
    public async Task InstallApksAsync(IReadOnlyList<string> files, InstanceViewModel? target)
    {
        var apks = files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var bundles = files.Where(f => f.EndsWith(".apks", StringComparison.OrdinalIgnoreCase)
                                       || f.EndsWith(".xapk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (apks.Count == 0 && bundles.Count == 0)
        {
            _main.ShowToast("Only .apk, .apks and .xapk files can be installed.", isError: true);
            return;
        }

        var running = Instances.Where(i => i.IsRunning).ToList();
        if (target is { IsRunning: false })
        {
            _main.ShowToast($"Start \"{target.Config.DisplayName}\" first, then drop the app again.", isError: true);
            return;
        }
        target ??= running.Count switch
        {
            0 => null,
            1 => running[0],
            _ => null,
        };
        if (target is null)
        {
            _main.ShowToast(running.Count == 0
                ? "Start an instance first, then drop the app again."
                : "Several instances are running. Drop the app on the one you want.", isError: true);
            return;
        }

        var instance = _services.Emulators.Get(target.Id);
        if (instance is null) return;

        var installer = new ApkInstaller(_services.Adb);
        foreach (var apk in apks)
            await RunInstall(target, Path.GetFileName(apk), () => installer.InstallApkAsync(instance.Serial, apk));
        foreach (var bundle in bundles)
            await RunInstall(target, Path.GetFileName(bundle), () => installer.InstallBundleAsync(instance.Serial, bundle));
    }

    private async Task RunInstall(InstanceViewModel target, string name, Func<Task<ApkInstallResult>> install)
    {
        target.Activity = $"Installing {name}…";
        try
        {
            var result = await install();
            _main.ShowToast(result.Success
                ? $"Installed {name} on \"{target.Config.DisplayName}\"."
                : $"Could not install {name}: {result.Message}", isError: !result.Success);
        }
        catch (Exception e)
        {
            Log.Error($"Installing {name} failed", e);
            _main.ShowToast($"Could not install {name}: {Format.Error(e)}", isError: true);
        }
        finally
        {
            target.Activity = null;
        }
    }
}
