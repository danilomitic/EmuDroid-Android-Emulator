using System.IO.Compression;
using EmuDroid.Core.Adb;

namespace EmuDroid.Tests;

public sealed class ApkInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "emudroid-apk-" + Guid.NewGuid().ToString("N"));
    public ApkInstallerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Extracts_apks_base_first_and_obb_targets()
    {
        string xapk = Path.Combine(_dir, "game.xapk");
        using (var zip = ZipFile.Open(xapk, ZipArchiveMode.Create))
        {
            zip.CreateEntry("config.x86_64.apk");
            zip.CreateEntry("base.apk");
            zip.CreateEntry("manifest.json");
            zip.CreateEntry("Android/obb/com.game/main.12.com.game.obb");
        }

        var c = ApkInstaller.ExtractBundle(xapk, Path.Combine(_dir, "out"));

        Assert.Equal(["base.apk", "config.x86_64.apk"], c.Apks.Select(Path.GetFileName));
        var obb = Assert.Single(c.ObbFiles);
        Assert.Equal("/sdcard/Android/obb/com.game/main.12.com.game.obb", obb.Remote);
    }

    [Theory]
    [InlineData(0, "Performing Streamed Install\nSuccess", true, "Installed.")]
    [InlineData(1, "adb: failed to install x.apk: Failure [INSTALL_FAILED_NO_MATCHING_ABIS: Failed to extract native libraries]", false, "this app only works on ARM")]
    [InlineData(1, "Failure [INSTALL_FAILED_OLDER_SDK]", false, "newer Android")]
    [InlineData(1, "adb: device offline", false, "adb: device offline")]
    public void Turns_adb_output_into_messages(int exit, string output, bool ok, string expected)
    {
        var r = ApkInstaller.ToResult(new CommandResult(exit, output, ""));
        Assert.Equal(ok, r.Success);
        Assert.Contains(expected, r.Message);
    }
}
