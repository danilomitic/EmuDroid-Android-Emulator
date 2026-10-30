using System.Net.Http;
using System.Reflection;
using EmuDroid.Core;
using EmuDroid.Core.Adb;
using EmuDroid.Core.Avd;
using EmuDroid.Core.Downloads;
using EmuDroid.Core.Sdk;

namespace EmuDroid.App.Services;

/// <summary>Shared services for the whole app (simple composition root, no DI container needed).</summary>
public sealed class AppServices : IDisposable
{
    private Task<SdkCatalog>? _catalog;

    public AppServices(string? root = null)
    {
        Paths = new AppPaths(root);
        Paths.EnsureCreated();
        Log.Init(Paths);
        Settings = AppSettings.Load(Paths);

        Http = new HttpClient { Timeout = TimeSpan.FromHours(3) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"EmuDroid/{Version}");
        Catalog = new CatalogService(Http);
        Installer = new PackageInstaller(Paths, new Downloader(Http));
        Avds = new AvdManager(Paths);
        Adb = new AdbClient(Paths.AdbExe);
        Emulators = new EmulatorHost(Paths, Adb);
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public HttpClient Http { get; }
    public CatalogService Catalog { get; }
    public PackageInstaller Installer { get; }
    public AvdManager Avds { get; }
    public AdbClient Adb { get; }
    public EmulatorHost Emulators { get; }

    /// <summary>Open keyboard &amp; mouse control sessions, per instance id.</summary>
    public Dictionary<string, Keymap.KeymapSession> KeymapSessions { get; } = [];

    public void CloseKeymap(string instanceId)
    {
        if (KeymapSessions.Remove(instanceId, out var session)) session.Close();
    }

    /// <summary>Google's catalog, loaded once per session. A failed load is retried next time.</summary>
    public Task<SdkCatalog> GetCatalogAsync()
    {
        if (_catalog is null || _catalog.IsFaulted || _catalog.IsCanceled)
            _catalog = Catalog.LoadAsync();
        return _catalog;
    }

    public void SaveSettings() => Settings.Save(Paths);

    public bool CoreComponentsInstalled =>
        File.Exists(Paths.EmulatorExe) && File.Exists(Paths.AdbExe);

    public bool IsImageInstalled(string systemImage) =>
        File.Exists(Path.Combine(Paths.PackageDirectory(systemImage), "system.img"));

    /// <summary>Makes sure emulator, platform-tools and the given system image are installed.</summary>
    public async Task EnsureInstalledAsync(SdkPackage? systemImage, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync();
        if (!File.Exists(Paths.AdbExe)) await Installer.InstallAsync(catalog.PlatformTools, progress, ct);
        if (!File.Exists(Paths.EmulatorExe)) await Installer.InstallAsync(catalog.Emulator, progress, ct);
        if (systemImage is not null && !IsImageInstalled(systemImage.Path))
            await Installer.InstallAsync(systemImage, progress, ct);
    }

    public void Dispose()
    {
        Emulators.Dispose();
        Http.Dispose();
    }
}
