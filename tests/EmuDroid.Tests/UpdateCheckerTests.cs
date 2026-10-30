using EmuDroid.Core.Updates;

namespace EmuDroid.Tests;

public class UpdateCheckerTests
{
    private static GitHubRelease Release(string tag, bool withSetup = true, bool prerelease = false) => new()
    {
        TagName = tag,
        Body = "Fixes",
        HtmlUrl = "https://github.com/danilomitic/EmuDroid-Android-Emulator/releases/tag/" + tag,
        Prerelease = prerelease,
        Assets = withSetup
            ? [new GitHubAsset { Name = "EmuDroid-Setup.exe", Size = 5_000_000, BrowserDownloadUrl = "https://example.test/EmuDroid-Setup.exe" },
               new GitHubAsset { Name = "EmuDroid-Setup.exe.sha256", BrowserDownloadUrl = "https://example.test/EmuDroid-Setup.exe.sha256" }]
            : [],
    };

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.1-beta", "2.0.1")]
    public void Parses_tags(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseTag(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Fact]
    public void Newer_release_with_setup_is_offered()
    {
        var u = UpdateChecker.Evaluate(Release("v1.1.0"), new Version(1, 0, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(1, 1, 0), u.Version);
        Assert.NotNull(u.Sha256Url);
    }

    [Theory]
    [InlineData("v1.0.0", true, false)]  // same version
    [InlineData("v0.9.0", true, false)]  // older
    [InlineData("v2.0.0", false, false)] // no installer attached
    [InlineData("v2.0.0", true, true)]   // prerelease
    [InlineData("nightly", true, false)] // unparseable
    public void Not_offered(string tag, bool withSetup, bool prerelease) =>
        Assert.Null(UpdateChecker.Evaluate(Release(tag, withSetup, prerelease), new Version(1, 0, 0)));
}
