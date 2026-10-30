using System.Globalization;
using System.Xml.Linq;

namespace EmuDroid.Core.Sdk;

/// <summary>
/// Parses Google's SDK repository XML (repository2-3.xml and sys-img2-3.xml).
/// Namespaces change between schema versions, so elements are matched by local name only.
/// </summary>
public static class RepositoryParser
{
    public sealed record Result(IReadOnlyList<SdkPackage> Packages, IReadOnlyDictionary<string, string> Licenses);

    public static Result Parse(XDocument doc, Uri baseUrl)
    {
        var root = doc.Root ?? throw new FormatException("Empty repository XML.");

        var channels = root.Elements().Where(e => e.Name.LocalName == "channel")
            .ToDictionary(e => (string?)e.Attribute("id") ?? "", e => e.Value.Trim());

        var licenses = root.Elements().Where(e => e.Name.LocalName == "license")
            .GroupBy(e => (string?)e.Attribute("id") ?? "")
            .ToDictionary(g => g.Key, g => g.First().Value.Trim());

        var packages = new List<SdkPackage>();
        foreach (var p in root.Elements().Where(e => e.Name.LocalName == "remotePackage"))
        {
            if (Child(p, "obsolete") is not null || (string?)p.Attribute("obsolete") == "true") continue;

            string path = (string?)p.Attribute("path") ?? "";
            string channelRef = (string?)Child(p, "channelRef")?.Attribute("ref") ?? "channel-0";
            string channel = channels.GetValueOrDefault(channelRef, "stable");

            var archives = Child(p, "archives")?.Elements().Where(e => e.Name.LocalName == "archive")
                .Select(a => ParseArchive(a, baseUrl))
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList() ?? [];

            packages.Add(new SdkPackage(
                Path: path,
                DisplayName: Child(p, "display-name")?.Value.Trim() ?? path,
                Revision: ParseRevision(Child(p, "revision")),
                LicenseRef: (string?)Child(p, "uses-license")?.Attribute("ref"),
                Channel: channel,
                Archives: archives,
                SystemImage: ParseSystemImage(Child(p, "type-details"))));
        }

        return new Result(packages, licenses);
    }

    /// <summary>Newest stable package with the given path that has a Windows x64 archive.</summary>
    public static SdkPackage? LatestStable(IEnumerable<SdkPackage> packages, string path) =>
        packages.Where(p => p.Path == path && p.Channel == "stable" && p.WindowsArchive is not null)
            .OrderByDescending(p => p.Revision)
            .FirstOrDefault();

    /// <summary>
    /// Plain Google Play x86_64 images (no "-ext" or 16k-page variants), one per API level, newest revision.
    /// </summary>
    public static IReadOnlyList<AndroidVersionOption> PlayStoreImages(IEnumerable<SdkPackage> packages, int minApi = 31)
    {
        var result = new List<AndroidVersionOption>();
        foreach (var group in packages
                     .Where(p => p.Channel == "stable" && p.WindowsArchive is not null)
                     .Select(p => (Package: p, Api: PlainApiLevel(p.Path)))
                     .Where(x => x.Api is not null && x.Api >= minApi)
                     .GroupBy(x => x.Api!.Value))
        {
            var newest = group.OrderByDescending(x => x.Package.Revision).First().Package;
            result.Add(new AndroidVersionOption(group.Key, AndroidName(group.Key), newest));
        }
        return result.OrderByDescending(v => v.ApiLevel).ToList();
    }

    /// <summary>Returns the API level for "system-images;android-NN;google_apis_playstore;x86_64", otherwise null.</summary>
    public static int? PlainApiLevel(string packagePath)
    {
        var parts = packagePath.Split(';');
        if (parts.Length != 4 || parts[0] != "system-images" || parts[2] != "google_apis_playstore" || parts[3] != "x86_64")
            return null;
        if (!parts[1].StartsWith("android-", StringComparison.Ordinal)) return null;
        return int.TryParse(parts[1]["android-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int api)
            ? api : null;
    }

    public static string AndroidName(int api) => api switch
    {
        31 => "Android 12",
        32 => "Android 12L",
        33 => "Android 13",
        34 => "Android 14",
        35 => "Android 15",
        36 => "Android 16",
        37 => "Android 17",
        _ => $"Android (API {api})",
    };

    private static XElement? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    private static Revision ParseRevision(XElement? r)
    {
        if (r is null) return new Revision(0);
        int Get(string n) => int.TryParse(Child(r, n)?.Value, out int v) ? v : 0;
        return new Revision(Get("major"), Get("minor"), Get("micro"), Get("preview"));
    }

    private static SdkArchive? ParseArchive(XElement a, Uri baseUrl)
    {
        var complete = Child(a, "complete");
        if (complete is null) return null;
        string? url = Child(complete, "url")?.Value.Trim();
        string? sha1 = Child(complete, "checksum")?.Value.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha1)) return null;
        long.TryParse(Child(complete, "size")?.Value, out long size);
        return new SdkArchive(
            new Uri(baseUrl, url),
            size,
            sha1.ToLowerInvariant(),
            Child(a, "host-os")?.Value.Trim(),
            Child(a, "host-arch")?.Value.Trim());
    }

    private static SystemImageInfo? ParseSystemImage(XElement? details)
    {
        if (details is null) return null;
        string? api = Child(details, "api-level")?.Value.Trim();
        string? abi = Child(details, "abi")?.Value.Trim();
        if (api is null || abi is null) return null;
        var tag = Child(details, "tag");
        return new SystemImageInfo(api, Child(tag ?? details, "id")?.Value.Trim() ?? "",
            Child(tag ?? details, "display")?.Value.Trim() ?? "", abi);
    }
}
