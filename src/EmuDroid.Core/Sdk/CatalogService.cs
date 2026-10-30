using System.Xml.Linq;

namespace EmuDroid.Core.Sdk;

/// <summary>Downloads Google's official SDK catalogs and picks the packages EmuDroid needs.</summary>
public sealed class CatalogService(HttpClient http)
{
    public static readonly Uri RepositoryBase = new("https://dl.google.com/android/repository/");
    public static readonly Uri RepositoryXml = new(RepositoryBase, "repository2-3.xml");
    public static readonly Uri PlayStoreBase = new(RepositoryBase, "sys-img/google_apis_playstore/");
    public static readonly Uri PlayStoreXml = new(PlayStoreBase, "sys-img2-3.xml");

    public async Task<SdkCatalog> LoadAsync(CancellationToken ct = default)
    {
        var repoTask = LoadXmlAsync(RepositoryXml, ct);
        var playTask = LoadXmlAsync(PlayStoreXml, ct);

        var repo = RepositoryParser.Parse(await repoTask, RepositoryBase);
        var play = RepositoryParser.Parse(await playTask, PlayStoreBase);

        var emulator = RepositoryParser.LatestStable(repo.Packages, "emulator")
            ?? throw new InvalidOperationException("Google's catalog has no stable Android Emulator for Windows.");
        var platformTools = RepositoryParser.LatestStable(repo.Packages, "platform-tools")
            ?? throw new InvalidOperationException("Google's catalog has no platform-tools for Windows.");
        var versions = RepositoryParser.PlayStoreImages(play.Packages);
        if (versions.Count == 0)
            throw new InvalidOperationException("Google's catalog has no Google Play system images.");

        var licenses = new Dictionary<string, string>(repo.Licenses);
        foreach (var (id, text) in play.Licenses) licenses.TryAdd(id, text);

        return new SdkCatalog(emulator, platformTools, versions, licenses);
    }

    private async Task<XDocument> LoadXmlAsync(Uri url, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(url, ct);
        return await XDocument.LoadAsync(stream, LoadOptions.None, ct);
    }
}
