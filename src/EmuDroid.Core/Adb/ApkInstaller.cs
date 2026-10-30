using System.IO.Compression;
using System.Security;
using System.Text.RegularExpressions;

namespace EmuDroid.Core.Adb;

public sealed record ApkInstallResult(bool Success, string Message);

/// <summary>Installs .apk, split .apks and .xapk (APKs + optional OBB data) files over ADB.</summary>
public sealed partial class ApkInstaller(AdbClient adb)
{
    public async Task<ApkInstallResult> InstallApkAsync(string serial, string apk, CancellationToken ct = default)
    {
        var r = await adb.InstallAsync(serial, [apk], ct);
        return ToResult(r);
    }

    public async Task<ApkInstallResult> InstallBundleAsync(string serial, string bundle, CancellationToken ct = default)
    {
        string temp = Path.Combine(Path.GetTempPath(), "emudroid-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var contents = ExtractBundle(bundle, temp);
            if (contents.Apks.Count == 0)
                return new ApkInstallResult(false, "The file does not contain any app (.apk) parts.");

            var r = await adb.InstallAsync(serial, contents.Apks, ct);
            if (!r.Success) return ToResult(r);

            foreach (var (local, remote) in contents.ObbFiles)
            {
                var push = await adb.RunAsync(serial, ["push", local, remote], TimeSpan.FromMinutes(20), ct);
                if (!push.Success) return new ApkInstallResult(false, "The app was installed, but its game data (OBB) could not be copied.");
            }
            return new ApkInstallResult(true, "Installed.");
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    public sealed record BundleContents(IReadOnlyList<string> Apks, IReadOnlyList<(string Local, string Remote)> ObbFiles);

    /// <summary>Extracts a bundle and finds its APK splits and OBB files ("Android/obb/&lt;package&gt;/*.obb").</summary>
    public static BundleContents ExtractBundle(string bundle, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        var apks = new List<string>();
        var obbs = new List<(string, string)>();

        using var zip = ZipFile.OpenRead(bundle);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException($"Blocked unsafe path in archive: {entry.FullName}");

            bool isApk = entry.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
            var obb = ObbPath().Match(entry.FullName.Replace('\\', '/'));
            if (!isApk && !obb.Success) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            if (isApk) apks.Add(dest);
            else obbs.Add((dest, $"/sdcard/Android/obb/{obb.Groups[1].Value}/{obb.Groups[2].Value}"));
        }

        // Base APK first keeps older adb versions happy.
        apks.Sort((a, b) => IsBase(b).CompareTo(IsBase(a)));
        return new BundleContents(apks, obbs);

        static bool IsBase(string p) => Path.GetFileName(p).StartsWith("base", StringComparison.OrdinalIgnoreCase);
    }

    public static ApkInstallResult ToResult(CommandResult r)
    {
        if (r.Success && r.Combined.Contains("Success", StringComparison.Ordinal))
            return new ApkInstallResult(true, "Installed.");

        var code = FailureCode().Match(r.Combined);
        string message = code.Success ? Explain(code.Groups[1].Value) : FirstLine(r.Combined);
        return new ApkInstallResult(false, message);
    }

    public static string Explain(string code) => code switch
    {
        "INSTALL_FAILED_NO_MATCHING_ABIS" => "this app only works on ARM devices and can't run on this instance.",
        "INSTALL_FAILED_OLDER_SDK" => "the app needs a newer Android version. Create an instance with a newer Android.",
        "INSTALL_FAILED_INSUFFICIENT_STORAGE" => "the instance is out of storage. Uninstall some apps and try again.",
        "INSTALL_FAILED_UPDATE_INCOMPATIBLE" => "a different version of this app is already installed. Uninstall it first.",
        "INSTALL_FAILED_VERSION_DOWNGRADE" => "a newer version of this app is already installed.",
        "INSTALL_PARSE_FAILED_NOT_APK" or "INSTALL_PARSE_FAILED_NO_CERTIFICATES" => "the file is damaged or not a valid app.",
        "INSTALL_FAILED_MISSING_SPLIT" => "the app is split into parts. Use the full .apks or .xapk file.",
        _ => $"Android refused the install ({code}).",
    };

    private static string FirstLine(string text)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length == 0 ? "unknown error." : line;
    }

    [GeneratedRegex(@"\[?(INSTALL_[A-Z_]+)")]
    private static partial Regex FailureCode();

    [GeneratedRegex(@"^Android/obb/([^/]+)/([^/]+\.obb)$", RegexOptions.IgnoreCase)]
    private static partial Regex ObbPath();
}
