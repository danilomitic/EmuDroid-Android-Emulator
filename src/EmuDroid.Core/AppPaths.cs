namespace EmuDroid.Core;

/// <summary>
/// All folders EmuDroid uses. Everything lives under %LocalAppData%\EmuDroid so no admin rights are needed.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmuDroid");
    }

    public string Root { get; }
    public string Sdk => Path.Combine(Root, "sdk");
    public string Avd => Path.Combine(Root, "avd");
    public string Keymaps => Path.Combine(Root, "keymaps");
    public string Logs => Path.Combine(Root, "logs");
    public string Downloads => Path.Combine(Root, "downloads");
    /// <summary>Emulator settings and locks (ANDROID_USER_HOME). The emulator expects it to exist.</summary>
    public string AndroidUser => Path.Combine(Root, "android-user");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string EmulatorExe => Path.Combine(Sdk, "emulator", "emulator.exe");
    public string AdbExe => Path.Combine(Sdk, "platform-tools", "adb.exe");

    /// <summary>Maps an SDK package path like "system-images;android-34;google_apis_playstore;x86_64" to a folder.</summary>
    public string PackageDirectory(string packagePath) =>
        Path.Combine([Sdk, .. packagePath.Split(';')]);

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Sdk, Avd, Keymaps, Logs, Downloads, AndroidUser })
            Directory.CreateDirectory(dir);
    }
}
