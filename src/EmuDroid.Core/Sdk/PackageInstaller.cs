using System.IO.Compression;
using System.Security;
using EmuDroid.Core.Downloads;

namespace EmuDroid.Core.Sdk;

public sealed record InstallProgress(string Stage, DownloadProgress? Download);

/// <summary>Downloads, verifies and extracts SDK packages into %LocalAppData%\EmuDroid\sdk.</summary>
public sealed class PackageInstaller(AppPaths paths, Downloader downloader)
{
    private const string MarkerFile = "package.xml";

    public bool IsInstalled(SdkPackage package)
    {
        var installed = InstalledRevision(package.Path);
        return installed is not null && installed.Value.CompareTo(package.Revision) >= 0;
    }

    public Revision? InstalledRevision(string packagePath)
    {
        string marker = Path.Combine(paths.PackageDirectory(packagePath), MarkerFile);
        if (!File.Exists(marker)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(marker);
            var rev = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "revision");
            if (rev is null) return null;
            int Get(string n) => int.TryParse(rev.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value, out int v) ? v : 0;
            return new Revision(Get("major"), Get("minor"), Get("micro"), Get("preview"));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public async Task InstallAsync(SdkPackage package, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled(package)) return;

        var archive = package.WindowsArchive
            ?? throw new InvalidOperationException($"{package.DisplayName} is not available for Windows x64.");

        paths.EnsureCreated();
        string zip = Path.Combine(paths.Downloads, Path.GetFileName(archive.Url.LocalPath));

        var dl = new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress($"Downloading {package.DisplayName}", p)));
        await downloader.DownloadAsync(archive.Url, zip, archive.Sha1, archive.Size, dl, ct);

        progress?.Report(new InstallProgress($"Installing {package.DisplayName}", null));
        string target = paths.PackageDirectory(package.Path);
        await Task.Run(() => ExtractFlattened(zip, target, ct), ct);
        WriteMarker(package, target);

        File.Delete(zip);
    }

    public void Uninstall(string packagePath)
    {
        string dir = paths.PackageDirectory(packagePath);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Extracts a zip into <paramref name="targetDir"/>. Google's zips have one top folder
    /// (e.g. "emulator/", "x86_64/"); its contents go straight into the target.
    /// </summary>
    public static void ExtractFlattened(string zipFile, string targetDir, CancellationToken ct = default)
    {
        string staging = targetDir + ".tmp";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        using (var zip = ZipFile.OpenRead(zipFile))
        {
            string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException($"Blocked unsafe path in archive: {entry.FullName}");

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        string source = staging;
        var dirs = Directory.GetDirectories(staging);
        if (dirs.Length == 1 && Directory.GetFiles(staging).Length == 0)
            source = dirs[0];

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        Directory.Move(source, targetDir);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }

    private static void WriteMarker(SdkPackage package, string target)
    {
        var r = package.Revision;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <ns2:repository xmlns:ns2="http://schemas.android.com/repository/android/common/02">
              <localPackage path="{SecurityElement.Escape(package.Path)}" obsolete="false">
                <revision><major>{r.Major}</major><minor>{r.Minor}</minor><micro>{r.Micro}</micro></revision>
                <display-name>{SecurityElement.Escape(package.DisplayName)}</display-name>
              </localPackage>
            </ns2:repository>
            """;
        File.WriteAllText(Path.Combine(target, MarkerFile), xml);
    }
}
