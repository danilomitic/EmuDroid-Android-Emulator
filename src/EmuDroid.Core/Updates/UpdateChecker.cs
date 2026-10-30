using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace EmuDroid.Core.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri SetupUrl, long SetupSize, Uri? Sha256Url, Uri PageUrl);

/// <summary>Checks GitHub Releases for a newer EmuDroid and installs it silently.</summary>
public sealed class UpdateChecker(HttpClient http, string owner = "danilomitic", string repo = "EmuDroid-Android-Emulator")
{
    public const string SetupAssetName = "EmuDroid-Setup.exe";

    public Uri LatestReleaseApi => new($"https://api.github.com/repos/{owner}/{repo}/releases/latest");

    /// <summary>Returns the newer release, or null when up to date (or no release exists yet).</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        return release is null ? null : Evaluate(release, current);
    }

    public static UpdateInfo? Evaluate(GitHubRelease release, Version current)
    {
        if (release.Draft || release.Prerelease) return null;
        if (!TryParseTag(release.TagName, out var version) || version <= current) return null;

        var setup = release.Assets.FirstOrDefault(a => a.Name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase));
        if (setup is null) return null;
        var sha = release.Assets.FirstOrDefault(a => a.Name.Equals(SetupAssetName + ".sha256", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(version, release.TagName, release.Body ?? "", new Uri(setup.BrowserDownloadUrl), setup.Size,
            sha is null ? null : new Uri(sha.BrowserDownloadUrl), new Uri(release.HtmlUrl));
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Downloads the installer, verifies its SHA-256 if published, and returns the local path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string downloadDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(downloadDir);
        string file = Path.Combine(downloadDir, $"EmuDroid-Setup-{update.Version}.exe");

        using (var response = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.SetupSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (update.Sha256Url is not null)
        {
            string expected = (await http.GetStringAsync(update.Sha256Url, ct)).Trim().Split(' ', '\t')[0];
            await using var stream = File.OpenRead(file);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidOperationException("The downloaded update is damaged (checksum mismatch). Please try again later.");
            }
        }
        return file;
    }

    /// <summary>Runs the installer silently. It closes EmuDroid, updates it and starts it again.</summary>
    public static void RunInstaller(string setupFile) =>
        Process.Start(new ProcessStartInfo(setupFile, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
        {
            UseShellExecute = true,
        });
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
}
