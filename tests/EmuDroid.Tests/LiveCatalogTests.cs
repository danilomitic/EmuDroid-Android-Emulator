using EmuDroid.Core.Sdk;
using Xunit.Abstractions;

namespace EmuDroid.Tests;

/// <summary>
/// Hits Google's real catalog. Runs only when EMUDROID_LIVE=1 so CI stays offline and fast.
/// </summary>
public class LiveCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_catalog_has_emulator_tools_and_play_images()
    {
        if (Environment.GetEnvironmentVariable("EMUDROID_LIVE") != "1") return;

        var catalog = await new CatalogService(new HttpClient()).LoadAsync();

        output.WriteLine($"Emulator {catalog.Emulator.Revision}: {catalog.Emulator.WindowsArchive!.Url}");
        output.WriteLine($"Platform-tools {catalog.PlatformTools.Revision}: {catalog.PlatformTools.WindowsArchive!.Url}");
        foreach (var v in catalog.AndroidVersions)
            output.WriteLine($"{v.Name} r{v.Package.Revision.Major} {v.DownloadSize / 1_000_000} MB {v.Package.WindowsArchive!.Url}");

        Assert.Contains(catalog.AndroidVersions, v => v.ApiLevel == 34);
        Assert.True(catalog.Licenses.ContainsKey("android-sdk-license"));
    }

    /// <summary>Installs emulator, platform-tools and Android 14 into the real %LocalAppData%\EmuDroid (EMUDROID_LIVE_FULL=1).</summary>
    [Fact]
    public async Task Real_full_install_android_14()
    {
        if (Environment.GetEnvironmentVariable("EMUDROID_LIVE_FULL") != "1") return;

        var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        var catalog = await new CatalogService(http).LoadAsync();
        var paths = new EmuDroid.Core.AppPaths();
        var installer = new PackageInstaller(paths, new EmuDroid.Core.Downloads.Downloader(http));
        var progress = new Progress<InstallProgress>(p =>
        {
            if (p.Download is { } d && d.BytesDone % (50 << 20) < (1 << 16)) Console.WriteLine($"{p.Stage} {d.Fraction:P0}");
        });

        await installer.InstallAsync(catalog.PlatformTools, progress);
        await installer.InstallAsync(catalog.Emulator, progress);
        await installer.InstallAsync(catalog.AndroidVersions.Single(v => v.ApiLevel == 34).Package, progress);

        Assert.True(File.Exists(paths.EmulatorExe));
    }

    [Fact]
    public async Task Real_platform_tools_install()
    {
        if (Environment.GetEnvironmentVariable("EMUDROID_LIVE") != "1") return;

        var http = new HttpClient();
        var catalog = await new CatalogService(http).LoadAsync();
        var paths = new EmuDroid.Core.AppPaths(Path.Combine(Path.GetTempPath(), "emudroid-live"));
        var installer = new PackageInstaller(paths, new EmuDroid.Core.Downloads.Downloader(http));

        await installer.InstallAsync(catalog.PlatformTools);

        Assert.True(File.Exists(paths.AdbExe));
        Assert.True(installer.IsInstalled(catalog.PlatformTools));
        output.WriteLine($"adb.exe installed at {paths.AdbExe}");
    }
}
