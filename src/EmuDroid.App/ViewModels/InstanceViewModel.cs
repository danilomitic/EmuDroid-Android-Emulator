using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmuDroid.App.Services;
using EmuDroid.App.Views;
using EmuDroid.Core;
using EmuDroid.Core.Avd;

namespace EmuDroid.App.ViewModels;

/// <summary>One instance card on the home screen.</summary>
public sealed partial class InstanceViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HomeViewModel _home;

    public InstanceViewModel(AppServices services, HomeViewModel home, InstanceConfig config)
    {
        _services = services;
        _home = home;
        _config = config;
        _state = services.Emulators.StateOf(config.Id);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private InstanceConfig _config;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRunning), nameof(IsStopped), nameof(IsBusy), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(EditCommand), nameof(CloneCommand),
        nameof(WipeCommand), nameof(DeleteCommand), nameof(RenameCommand))]
    private InstanceState _state;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _activity;
    [ObservableProperty] private double _activityProgress;

    public string Id => Config.Id;
    public string Subtitle => $"{Config.AndroidName}, {Config.RamMb / 1024.0:0.#} GB RAM, {Config.CpuCores} cores";
    public bool IsRunning => State == InstanceState.Running;
    public bool IsStopped => State == InstanceState.Stopped;
    public bool IsBusy => State is InstanceState.Starting or InstanceState.Booting or InstanceState.Stopping || Activity is not null;
    public bool CanEdit => IsStopped && Activity is null;

    partial void OnActivityChanged(string? value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        StartCommand.NotifyCanExecuteChanged();
    }

    public string StatusText => Activity ?? State switch
    {
        InstanceState.Starting => "Starting…",
        InstanceState.Booting => "Android is starting…",
        InstanceState.Running => "Running",
        InstanceState.Stopping => "Stopping…",
        _ => "Stopped",
    };

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartAsync()
    {
        Error = null;
        try
        {
            if (!_services.IsImageInstalled(Config.SystemImage) || !_services.CoreComponentsInstalled)
            {
                var catalog = await _services.GetCatalogAsync();
                var image = catalog.AndroidVersions.FirstOrDefault(v => v.Package.Path == Config.SystemImage)?.Package;
                await RunWithProgressAsync("Downloading", p => _services.EnsureInstalledAsync(image, p, CancellationToken.None));
            }
            _services.Emulators.Start(Config);
        }
        catch (Exception e)
        {
            Log.Error($"Starting {Config.Id} failed", e);
            Error = Format.Error(e);
        }
    }

    public async Task RunWithProgressAsync(string label, Func<IProgress<Core.Sdk.InstallProgress>, Task> work)
    {
        Activity = label + "…";
        var progress = new Progress<Core.Sdk.InstallProgress>(p =>
        {
            Activity = p.Download is { } d ? $"{p.Stage} {d.Fraction:P0}" : p.Stage + "…";
            ActivityProgress = p.Download?.Fraction ?? ActivityProgress;
        });
        try
        {
            await work(progress);
        }
        finally
        {
            Activity = null;
            ActivityProgress = 0;
        }
    }

    private bool CanStop() => State is InstanceState.Running or InstanceState.Booting;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            await _services.Emulators.StopAsync(Config.Id);
        }
        catch (Exception e)
        {
            Log.Error($"Stopping {Config.Id} failed", e);
            Error = Format.Error(e);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync() => _home.EditInstanceAsync(this);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Rename()
    {
        string? name = DialogWindow.Prompt("Rename instance", "", Config.DisplayName, "Rename");
        if (name is null) return;
        Config = _services.Avds.Rename(Config, name);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task CloneAsync()
    {
        string? name = DialogWindow.Prompt("Clone instance",
            "The copy gets the same apps, settings and accounts.", Config.DisplayName + " copy", "Clone");
        if (name is null) return;
        try
        {
            Activity = "Cloning…";
            var clone = await Task.Run(() => _services.Avds.Clone(Config, name));
            _home.Add(clone);
        }
        catch (Exception e)
        {
            Log.Error($"Cloning {Config.Id} failed", e);
            Error = Format.Error(e);
        }
        finally
        {
            Activity = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Wipe()
    {
        if (!DialogWindow.Confirm("Reset to factory settings?",
                $"All apps, accounts and files in \"{Config.DisplayName}\" will be erased. The instance itself stays.", "Erase data", danger: true))
            return;
        try
        {
            _services.Avds.WipeData(Config);
        }
        catch (Exception e)
        {
            Error = Format.Error(e);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Delete()
    {
        if (!DialogWindow.Confirm($"Delete \"{Config.DisplayName}\"?",
                "The instance and everything in it will be removed from this PC.", "Delete instance", danger: true))
            return;
        try
        {
            _services.Avds.Delete(Config);
            _home.Remove(this);
        }
        catch (Exception e)
        {
            Error = Format.Error(e);
        }
    }

    [RelayCommand]
    private void OpenFolder() =>
        Process.Start(new ProcessStartInfo(_services.Avds.AvdDirectory(Config.Id)) { UseShellExecute = true });

    [RelayCommand]
    private void DismissError() => Error = null;
}
