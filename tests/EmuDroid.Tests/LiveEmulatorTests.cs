using System.Diagnostics;
using EmuDroid.Core;
using EmuDroid.Core.Adb;
using EmuDroid.Core.Avd;
using EmuDroid.Keymap;
using Xunit.Abstractions;

namespace EmuDroid.Tests;

/// <summary>
/// Boots real Android instances with the installed emulator and Android 14 image in %LocalAppData%\EmuDroid.
/// Needs Windows Hypervisor Platform, so it runs only when EMUDROID_LIVE_EMU=1. Takes several minutes.
/// </summary>
public class LiveEmulatorTests(ITestOutputHelper output)
{
    private const string Image = "system-images;android-34;google_apis_playstore;x86_64";

    [Fact]
    public async Task Boots_android_with_play_installs_apks_runs_two_instances_and_takes_touches()
    {
        if (Environment.GetEnvironmentVariable("EMUDROID_LIVE_EMU") != "1") return;

        var paths = new AppPaths();
        var avds = new AvdManager(paths);
        var adb = new AdbClient(paths.AdbExe);
        using var host = new EmulatorHost(paths, adb);
        host.StateChanged += (_, e) => Log($"state {e.InstanceId}: {e.State} {e.Error}");
        string? screenshotDir = Environment.GetEnvironmentVariable("EMUDROID_LIVE_SHOTS");

        var a = avds.Create("Live Test A", 34, Image);
        InstanceConfig? b = null;
        try
        {
            // 1. Boot
            var sw = Stopwatch.StartNew();
            var runA = host.Start(a, coldBoot: true);
            await WaitRunning(host, a.Id, TimeSpan.FromMinutes(10));
            Log($"A booted in {sw.Elapsed.TotalSeconds:F0}s on {runA.Serial}, gRPC {runA.GrpcPort}");

            // 2. Android version, Google Play and Play Services
            Assert.Equal("14", await Shell(adb, runA.Serial, "getprop ro.build.version.release"));
            string packages = await Shell(adb, runA.Serial, "pm list packages");
            Assert.Contains("package:com.android.vending", packages);
            Assert.Contains("package:com.google.android.gms", packages);
            Log("Play Store version: " + await Shell(adb, runA.Serial, "dumpsys package com.android.vending | grep -m1 versionName"));
            Log("Screen: " + await Shell(adb, runA.Serial, "wm size"));

            await Shell(adb, runA.Serial, "monkey -p com.android.vending -c android.intent.category.LAUNCHER 1");
            await WaitFor(async () => await adb.ForegroundPackageAsync(runA.Serial) == "com.android.vending", TimeSpan.FromSeconds(60));
            Log("Play Store opened in the foreground");
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (screenshotDir is not null) await Screenshot(adb, runA.Serial, Path.Combine(screenshotDir, "play-store.png"));

            // 3. APK install: a real APK pulled from the device, then a broken file
            var installer = new ApkInstaller(adb);
            string apk = await PullSomeApk(adb, runA.Serial);
            var ok = await installer.InstallApkAsync(runA.Serial, apk);
            Log($"Install {Path.GetFileName(apk)}: {ok.Success} {ok.Message}");
            Assert.True(ok.Success, ok.Message);

            string broken = Path.Combine(Path.GetTempPath(), "emudroid-broken.apk");
            await File.WriteAllTextAsync(broken, "not an apk");
            var bad = await installer.InstallApkAsync(runA.Serial, broken);
            Log($"Install broken file: {bad.Success} {bad.Message}");
            Assert.False(bad.Success);

            // 4. Keyboard controls path: emulator window + touches over gRPC reach Android's touch screen
            IntPtr hwnd = Native.FindEmulatorWindow(runA.ConsolePort, a.Id);
            Log($"Emulator window found: {hwnd != IntPtr.Zero}");
            Assert.NotEqual(IntPtr.Zero, hwnd);
            var rect = Native.ClientScreenRect(hwnd);
            Log($"Window client area: {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");

            await Shell(adb, runA.Serial, "input keyevent KEYCODE_HOME");
            var events = adb.RunAsync(runA.Serial, ["shell", "timeout 6 getevent -l"], TimeSpan.FromSeconds(15));
            await Task.Delay(1500);
            using (var touch = new TouchSender(runA.GrpcPort, a.Width, a.Height))
            {
                await touch.TapAsync(10, 0.5, 0.5);
                // two fingers at once, like joystick + aim
                await touch.DownAsync(1, 0.2, 0.7);
                await touch.DownAsync(2, 0.8, 0.4);
                await touch.DownAsync(1, 0.25, 0.65);
                await touch.UpAsync(1, 0.25, 0.65);
                await touch.UpAsync(2, 0.8, 0.4);
            }
            string ev = (await events).Output;
            int touchesSeen = ev.Split('\n').Count(l => l.Contains("ABS_MT_POSITION_X"));
            Log($"Android saw {touchesSeen} touch position events");
            Assert.Contains("ABS_MT_TRACKING_ID", ev);
            Assert.True(touchesSeen >= 3);

            // 5. Second instance at the same time (a clone of A would copy the running disk, so make a fresh one)
            b = avds.Create("Live Test B", 34, Image);
            sw.Restart();
            var runB = host.Start(b, coldBoot: true);
            await WaitRunning(host, b.Id, TimeSpan.FromMinutes(10));
            Log($"B booted in {sw.Elapsed.TotalSeconds:F0}s on {runB.Serial}, gRPC {runB.GrpcPort}");
            Assert.NotEqual(runA.ConsolePort, runB.ConsolePort);
            Assert.NotEqual(runA.GrpcPort, runB.GrpcPort);
            Assert.Equal(InstanceState.Running, host.StateOf(a.Id));
            Assert.Equal("1", await Shell(adb, runB.Serial, "getprop sys.boot_completed"));
            Log("Both instances running at the same time");

            // 6. Stop
            await host.StopAsync(b.Id);
            await host.StopAsync(a.Id);
            Assert.True(runA.Process.HasExited && runB.Process.HasExited);
            Log("Both stopped");
        }
        finally
        {
            foreach (var r in host.Running.ToList()) try { r.Process.Kill(true); } catch { }
            await Task.Delay(2000);
            try { avds.Delete(a); } catch (Exception e) { Log("cleanup A: " + e.Message); }
            if (b is not null) try { avds.Delete(b); } catch (Exception e) { Log("cleanup B: " + e.Message); }
        }
    }

    private void Log(string s) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");

    private static async Task<string> Shell(AdbClient adb, string serial, string cmd) =>
        (await adb.RunAsync(serial, ["shell", cmd], TimeSpan.FromSeconds(60))).Output.Trim();

    private static async Task WaitRunning(EmulatorHost host, string id, TimeSpan max) =>
        await WaitFor(() => Task.FromResult(host.StateOf(id) switch
        {
            InstanceState.Running => true,
            InstanceState.Stopped => throw new InvalidOperationException($"{id} stopped while booting, see logs/emulator-{id}.log"),
            _ => false,
        }), max);

    private static async Task WaitFor(Func<Task<bool>> check, TimeSpan max)
    {
        var deadline = DateTime.UtcNow + max;
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(2000);
        }
    }

    private static async Task Screenshot(AdbClient adb, string serial, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await adb.RunAsync(serial, ["shell", "screencap -p /sdcard/s.png"], TimeSpan.FromSeconds(30));
        await adb.RunAsync(serial, ["pull", "/sdcard/s.png", file], TimeSpan.FromSeconds(30));
    }

    /// <summary>Pulls a small app that ships on the image, so the install test needs no download.</summary>
    private static async Task<string> PullSomeApk(AdbClient adb, string serial)
    {
        string[] candidates = ["com.android.calculator2", "com.google.android.calculator", "com.android.camera2", "com.android.contacts", "com.android.dialer"];
        foreach (var pkg in candidates)
        {
            string path = (await Shell(adb, serial, $"pm path {pkg}")).Split('\n').FirstOrDefault(l => l.StartsWith("package:"))?.Substring(8).Trim() ?? "";
            if (path.Length == 0) continue;
            string local = Path.Combine(Path.GetTempPath(), pkg + ".apk");
            var r = await adb.RunAsync(serial, ["pull", path, local], TimeSpan.FromSeconds(60));
            if (r.Success && File.Exists(local)) return local;
        }
        throw new InvalidOperationException("No small app found on the image to reinstall.");
    }
}
