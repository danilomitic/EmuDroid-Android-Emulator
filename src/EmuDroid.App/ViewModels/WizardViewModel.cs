using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmuDroid.App.Services;
using EmuDroid.Core;
using EmuDroid.Core.Host;
using EmuDroid.Core.Sdk;

namespace EmuDroid.App.ViewModels;

public enum WizardStep { Welcome, SystemCheck, License, Version, Download, Done }

public sealed partial class WizardStepItem(int number, string title) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
}

/// <summary>First-run setup: system check → license → Android version → download → first instance.</summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action _onFinished;
    private CancellationTokenSource? _downloadCts;

    public WizardViewModel(AppServices services, Action onFinished)
    {
        _services = services;
        _onFinished = onFinished;
        Steps =
        [
            new(1, "Check your PC"),
            new(2, "Accept license"),
            new(3, "Choose Android"),
            new(4, "Download"),
        ];
        UpdateSteps();
    }

    public ObservableCollection<WizardStepItem> Steps { get; }
    public ObservableCollection<AndroidVersionOption> Versions { get; } = [];

    [ObservableProperty] private WizardStep _step = WizardStep.Welcome;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    // System check
    [ObservableProperty] private SystemCheckResult? _check;
    [ObservableProperty] private string _checkTitle = "";
    [ObservableProperty] private string _checkMessage = "";
    [ObservableProperty] private bool _canEnableWhpx;
    [ObservableProperty] private bool _needsRestart;
    [ObservableProperty] private bool _showBiosGuide;
    [ObservableProperty] private string _biosGuide = "";

    // License
    [ObservableProperty] private string _licenseText = "Loading license from Google…";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool _licenseAccepted;

    // Version
    [ObservableProperty] private AndroidVersionOption? _selectedVersion;
    [ObservableProperty] private string _instanceName = "My Android";

    // Download
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    partial void OnStepChanged(WizardStep value)
    {
        UpdateSteps();
        NextCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSteps()
    {
        int current = Step switch
        {
            WizardStep.SystemCheck => 1,
            WizardStep.License => 2,
            WizardStep.Version => 3,
            WizardStep.Download => 4,
            WizardStep.Done => 5,
            _ => 0,
        };
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Number == current;
            s.IsDone = s.Number < current;
        }
    }

    private bool CanGoNext() => !IsBusy && Step switch
    {
        WizardStep.SystemCheck => Check is not null,
        WizardStep.License => LicenseAccepted,
        WizardStep.Version => SelectedVersion is not null,
        WizardStep.Download => false,
        _ => true,
    };

    partial void OnIsBusyChanged(bool value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnCheckChanged(SystemCheckResult? value) => NextCommand.NotifyCanExecuteChanged();
    partial void OnSelectedVersionChanged(AndroidVersionOption? value) => NextCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        Error = null;
        switch (Step)
        {
            case WizardStep.Welcome:
                Step = WizardStep.SystemCheck;
                await RunCheckAsync();
                break;
            case WizardStep.SystemCheck:
                Step = WizardStep.License;
                await LoadCatalogAsync();
                break;
            case WizardStep.License:
                _services.Settings.LicenseAccepted = true;
                _services.SaveSettings();
                Step = WizardStep.Version;
                break;
            case WizardStep.Version:
                Step = WizardStep.Download;
                await DownloadAsync();
                break;
            case WizardStep.Done:
                _services.Settings.FirstRunCompleted = true;
                _services.SaveSettings();
                _onFinished();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy) return;
        Error = null;
        Step = Step switch
        {
            WizardStep.SystemCheck => WizardStep.Welcome,
            WizardStep.License => WizardStep.SystemCheck,
            WizardStep.Version => WizardStep.License,
            WizardStep.Download => WizardStep.Version,
            _ => Step,
        };
    }

    [RelayCommand]
    private async Task RunCheckAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => VirtualizationChecker.Check(_services.Paths.Root));
            Check = result;
            var f = result.Facts;
            CanEnableWhpx = result.Verdict == VirtualizationVerdict.NeedsWhpx;
            NeedsRestart = result.Verdict == VirtualizationVerdict.NeedsRestart;
            ShowBiosGuide = result.Verdict == VirtualizationVerdict.BiosDisabled;
            BiosGuide = VirtualizationChecker.BiosGuide(f.CpuName);
            (CheckTitle, CheckMessage) = result.Verdict switch
            {
                VirtualizationVerdict.Ready => ("Your PC is ready", $"{f.CpuName} with {f.TotalRamGb:0} GB of RAM. Hardware acceleration is on, so Android will run fast."),
                VirtualizationVerdict.NeedsWhpx => ("One Windows feature needs to be turned on",
                    "Android needs Windows Hypervisor Platform to run fast. EmuDroid can turn it on for you. Windows will ask for permission and then needs a restart."),
                VirtualizationVerdict.NeedsRestart => ("Restart to finish",
                    "Windows Hypervisor Platform is turned on, but Windows has not restarted since. Restart your PC, then open EmuDroid again."),
                _ => ("Virtualization is turned off in BIOS",
                    "Your processor supports it, but it is switched off in the BIOS settings. Follow the steps below, then open EmuDroid again."),
            };
            if (result.Warnings.Count > 0) CheckMessage += "\n\n" + string.Join("\n", result.Warnings);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnableWhpxAsync()
    {
        Error = null;
        IsBusy = true;
        CheckTitle = "Turning on Windows Hypervisor Platform…";
        CheckMessage = "This can take a minute. Approve the Windows permission prompt if it appears.";
        try
        {
            bool restart = await VirtualizationChecker.EnableWhpxAsync();
            Log.Info($"WHPX enabled, restart needed: {restart}");
            await RunCheckAsync();
            if (restart)
            {
                NeedsRestart = true;
                CanEnableWhpx = false;
                CheckTitle = "Restart to finish";
                CheckMessage = "Windows Hypervisor Platform is on. Restart your PC, then open EmuDroid again. Setup continues where you left off.";
            }
        }
        catch (OperationCanceledException)
        {
            Error = "Windows permission was not granted, so nothing was changed.";
            await RunCheckAsync();
        }
        catch (Exception e)
        {
            Log.Error("Enabling WHPX failed", e);
            Error = Format.Error(e);
            await RunCheckAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RestartNow()
    {
        if (Views.DialogWindow.Confirm("Restart now?", "Save your work in other programs first.", "Restart"))
            VirtualizationChecker.RestartWindows();
    }

    private async Task LoadCatalogAsync()
    {
        IsBusy = true;
        try
        {
            var catalog = await _services.GetCatalogAsync();
            LicenseText = catalog.Licenses.GetValueOrDefault("android-sdk-license", "License text unavailable.");
            Versions.Clear();
            foreach (var v in catalog.AndroidVersions) Versions.Add(v);
            SelectedVersion = Versions.FirstOrDefault(v => v.ApiLevel == 34) ?? Versions.FirstOrDefault();
            LicenseAccepted = _services.Settings.LicenseAccepted;
        }
        catch (Exception e)
        {
            Log.Error("Loading Google catalog failed", e);
            LicenseText = "";
            Error = Format.Error(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RetryCatalogAsync() => LoadCatalogAsync();

    private async Task DownloadAsync()
    {
        if (SelectedVersion is null) return;
        IsBusy = true;
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressText = Format.Progress(p);
            Progress = p.Download?.Fraction ?? Progress;
        });
        try
        {
            await _services.EnsureInstalledAsync(SelectedVersion.Package, progress, _downloadCts.Token);
            string name = string.IsNullOrWhiteSpace(InstanceName) ? "My Android" : InstanceName;
            _services.Avds.Create(name, SelectedVersion.ApiLevel, SelectedVersion.Package.Path);
            Log.Info($"First-run setup finished with {SelectedVersion.Name}");
            Step = WizardStep.Done;
        }
        catch (Exception e)
        {
            Log.Error("First-run download failed", e);
            Error = Format.Error(e) + " Already downloaded parts are kept, so trying again continues where it stopped.";
        }
        finally
        {
            IsBusy = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private async Task RetryDownloadAsync()
    {
        Error = null;
        await DownloadAsync();
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();
}
