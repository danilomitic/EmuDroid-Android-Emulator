namespace EmuDroid.Core.Sdk;

/// <summary>Package revision as used by Google's SDK repository (major.minor.micro[-preview]).</summary>
public readonly record struct Revision(int Major, int Minor = 0, int Micro = 0, int Preview = 0) : IComparable<Revision>
{
    public int CompareTo(Revision other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Micro.CompareTo(other.Micro);
        if (c != 0) return c;
        // A stable release (Preview == 0) is newer than any preview of the same version.
        if (Preview == other.Preview) return 0;
        if (Preview == 0) return 1;
        if (other.Preview == 0) return -1;
        return Preview.CompareTo(other.Preview);
    }

    public static bool operator >(Revision a, Revision b) => a.CompareTo(b) > 0;
    public static bool operator <(Revision a, Revision b) => a.CompareTo(b) < 0;

    public override string ToString() =>
        Preview > 0 ? $"{Major}.{Minor}.{Micro} rc{Preview}" : $"{Major}.{Minor}.{Micro}";
}

public sealed record SdkArchive(Uri Url, long Size, string Sha1, string? HostOs, string? HostArch)
{
    public bool IsForWindowsX64 =>
        (HostOs is null || HostOs == "windows") && (HostArch is null || HostArch == "x64");
}

public sealed record SdkPackage(
    string Path,
    string DisplayName,
    Revision Revision,
    string? LicenseRef,
    string Channel,
    IReadOnlyList<SdkArchive> Archives,
    SystemImageInfo? SystemImage = null)
{
    public SdkArchive? WindowsArchive => Archives.FirstOrDefault(a => a.IsForWindowsX64);
}

public sealed record SystemImageInfo(string ApiLevel, string TagId, string TagDisplay, string Abi);

/// <summary>A Google Play system image the user can pick in the UI.</summary>
public sealed record AndroidVersionOption(int ApiLevel, string Name, SdkPackage Package)
{
    public long DownloadSize => Package.WindowsArchive?.Size ?? 0;
    public override string ToString() => Name;
}

public sealed record SdkCatalog(
    SdkPackage Emulator,
    SdkPackage PlatformTools,
    IReadOnlyList<AndroidVersionOption> AndroidVersions,
    IReadOnlyDictionary<string, string> Licenses);
