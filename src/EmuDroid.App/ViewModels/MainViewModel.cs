using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmuDroid.App.Services;
using EmuDroid.Core;
using EmuDroid.Core.Updates;

namespace EmuDroid.App.ViewModels;

public enum Page { Home, Settings }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(1) };

    public MainViewModel(AppServices services)
    {
        _services = services;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };
        Updates = new UpdateViewModel(services, this);

        if (services.Settings.FirstRunCompleted)
            ShowShell();
        else
            _current = new WizardViewModel(services, ShowShell);
    }

    public UpdateViewModel Updates { get; }

    [ObservableProperty] private object? _current;
    [ObservableProperty] private HomeViewModel? _home;
    [ObservableProperty] private SettingsViewModel? _settings;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsSettings))] private Page _page = Page.Home;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    public bool IsHome { get => Page == Page.Home; set { if (value) Page = Page.Home; } }
    public bool IsSettings { get => Page == Page.Settings; set { if (value) Page = Page.Settings; } }
    public string VersionText => $"Version {AppServices.Version}";

    private void ShowShell()
    {
        Home = new HomeViewModel(_services, this);
        Settings = new SettingsViewModel(_services, this);
        Current = this;
        _ = Updates.CheckOnStartupAsync();
        _updateTimer.Tick += (_, _) => _ = Updates.CheckOnStartupAsync();
        _updateTimer.Start();
    }

    public void ShowToast(string message, bool isError = false)
    {
        Toast = message;
        ToastIsError = isError;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    /// <summary>Lets Settings re-run the setup wizard (e.g. after enabling virtualization).</summary>
    public void RunWizard() => Current = new WizardViewModel(_services, ShowShell);
}

public sealed partial class UpdateViewModel(AppServices services, MainViewModel main) : ObservableObject
{
    private readonly UpdateChecker _checker = new(services.Http);

    [ObservableProperty] private UpdateInfo? _available;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _status = "";

    public async Task CheckOnStartupAsync()
    {
        var s = services.Settings;
        if (!s.AutoUpdate) return;
        if (s.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromDays(1)) return;
        await CheckAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (IsWorking) return;
        IsWorking = true;
        Status = "Checking for updates…";
        try
        {
            var current = Version.Parse(AppServices.Version);
            var update = await _checker.CheckAsync(current);
            services.Settings.LastUpdateCheck = DateTimeOffset.Now;
            services.SaveSettings();

            if (update is not null && (manual || services.Settings.SkippedVersion != update.Tag))
            {
                Available = update;
                Status = $"EmuDroid {update.Version} is available.";
            }
            else
            {
                Status = "EmuDroid is up to date.";
            }
        }
        catch (Exception e)
        {
            Log.Error("Update check failed", e);
            Status = manual ? "Could not check for updates. Check your internet connection." : "";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is null) return;
        if (services.Emulators.Running.Count > 0 &&
            !Views.DialogWindow.Confirm("Install update now?", "Running instances will be closed while EmuDroid updates.", "Update"))
            return;

        IsWorking = true;
        try
        {
            var progress = new Progress<double>(p => Status = $"Downloading update {p:P0}");
            string file = await _checker.DownloadAsync(Available, services.Paths.Downloads, progress);
            foreach (var r in services.Emulators.Running.ToList()) await services.Emulators.StopAsync(r.Config.Id);
            Log.Info($"Installing update {Available.Version}");
            UpdateChecker.RunInstaller(file);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            Log.Error("Update failed", e);
            main.ShowToast("Update failed: " + Format.Error(e), isError: true);
            Status = "";
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (Available is null) return;
        services.Settings.SkippedVersion = Available.Tag;
        services.SaveSettings();
        Available = null;
    }

    [RelayCommand]
    private void ShowNotes()
    {
        if (Available is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Available.PageUrl.ToString()) { UseShellExecute = true });
    }
}
