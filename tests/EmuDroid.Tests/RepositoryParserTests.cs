using System.Xml.Linq;
using EmuDroid.Core.Sdk;

namespace EmuDroid.Tests;

public class RepositoryParserTests
{
    private static RepositoryParser.Result Load(string fixture, Uri baseUrl) =>
        RepositoryParser.Parse(XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), baseUrl);

    [Fact]
    public void Picks_stable_emulator_not_dev_or_obsolete()
    {
        var repo = Load("repository-sample.xml", CatalogService.RepositoryBase);

        var emulator = RepositoryParser.LatestStable(repo.Packages, "emulator");

        Assert.NotNull(emulator);
        Assert.Equal(new Revision(37, 2, 13), emulator.Revision);
        Assert.Equal("android-sdk-license", emulator.LicenseRef);
        Assert.Equal("https://dl.google.com/android/repository/emulator-windows_x64-1.zip",
            emulator.WindowsArchive!.Url.ToString());
    }

    [Fact]
    public void Platform_tools_uses_windows_archive_with_lowercase_checksum()
    {
        var repo = Load("repository-sample.xml", CatalogService.RepositoryBase);

        var tools = RepositoryParser.LatestStable(repo.Packages, "platform-tools")!;

        Assert.Equal("platform-tools_r37.0.1-win.zip", tools.WindowsArchive!.Url.Segments[^1]);
        Assert.Equal("e03e78b1d80b396f1c3358e31251cb31740e1110", tools.WindowsArchive.Sha1);
        Assert.Equal(8044989, tools.WindowsArchive.Size);
    }

    [Fact]
    public void Reads_license_texts()
    {
        var repo = Load("repository-sample.xml", CatalogService.RepositoryBase);
        Assert.StartsWith("Terms and Conditions", repo.Licenses["android-sdk-license"]);
    }

    [Fact]
    public void Play_images_keep_only_plain_stable_images_newest_revision()
    {
        var play = Load("playstore-sample.xml", CatalogService.PlayStoreBase);

        var versions = RepositoryParser.PlayStoreImages(play.Packages);

        Assert.Equal([35, 34], versions.Select(v => v.ApiLevel));
        var android14 = versions.Single(v => v.ApiLevel == 34);
        Assert.Equal("Android 14", android14.Name);
        Assert.Equal(new Revision(14), android14.Package.Revision);
        Assert.Equal("https://dl.google.com/android/repository/sys-img/google_apis_playstore/x86_64-34_r14.zip",
            android14.Package.WindowsArchive!.Url.ToString());
        Assert.Equal("google_apis_playstore", android14.Package.SystemImage!.TagId);
    }

    [Theory]
    [InlineData("system-images;android-34;google_apis_playstore;x86_64", 34)]
    [InlineData("system-images;android-34-ext10;google_apis_playstore;x86_64", null)]
    [InlineData("system-images;android-35;google_apis_playstore_ps16k;x86_64", null)]
    [InlineData("system-images;android-34;google_apis;x86_64", null)]
    [InlineData("system-images;android-34;google_apis_playstore;arm64-v8a", null)]
    [InlineData("emulator", null)]
    public void PlainApiLevel_only_matches_plain_play_images(string path, int? expected) =>
        Assert.Equal(expected, RepositoryParser.PlainApiLevel(path));

    [Fact]
    public void Revision_ordering()
    {
        Assert.True(new Revision(37, 2, 13) > new Revision(37, 2, 9));
        Assert.True(new Revision(37, 3) > new Revision(37, 2, 99));
        Assert.True(new Revision(1, 0, 0) > new Revision(1, 0, 0, Preview: 3));
        Assert.True(new Revision(1, 0, 0, 2) < new Revision(1, 0, 0, 3));
    }
}
