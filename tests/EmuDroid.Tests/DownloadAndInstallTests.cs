using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using EmuDroid.Core.Downloads;
using EmuDroid.Core.Sdk;

namespace EmuDroid.Tests;

public sealed class DownloadAndInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "emudroid-tests-" + Guid.NewGuid().ToString("N"));

    public DownloadAndInstallTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    /// <summary>Serves a byte array and honours "Range: bytes=N-" like Google's CDN.</summary>
    private sealed class FakeServer(byte[] data) : HttpMessageHandler
    {
        public List<long?> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long? from = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(from);
            var body = from is null ? data : data[(int)from.Value..];
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
            };
            if (from is not null)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Downloads_and_verifies_checksum()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var server = new FakeServer(data);
        var target = Path.Combine(_dir, "file.zip");

        await new Downloader(new HttpClient(server)).DownloadAsync(
            new Uri("https://example.test/file.zip"), target, Sha1(data), data.Length);

        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Resumes_from_partial_file()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var server = new FakeServer(data);
        var target = Path.Combine(_dir, "file.zip");
        await File.WriteAllBytesAsync(target + ".part", data[..120_000]);

        await new Downloader(new HttpClient(server)).DownloadAsync(
            new Uri("https://example.test/file.zip"), target, Sha1(data), data.Length);

        Assert.Equal([120_000L], server.RangeStarts);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Bad_checksum_throws_and_removes_partial_file()
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        var target = Path.Combine(_dir, "file.zip");

        await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            new Downloader(new HttpClient(new FakeServer(data))).DownloadAsync(
                new Uri("https://example.test/file.zip"), target, new string('0', 40), data.Length));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Skips_download_when_file_already_valid()
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        var server = new FakeServer(data);
        var target = Path.Combine(_dir, "file.zip");
        await File.WriteAllBytesAsync(target, data);

        await new Downloader(new HttpClient(server)).DownloadAsync(
            new Uri("https://example.test/file.zip"), target, Sha1(data), data.Length);

        Assert.Empty(server.RangeStarts);
    }

    [Fact]
    public void Extract_flattens_single_top_folder()
    {
        string zipPath = Path.Combine(_dir, "pkg.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "emulator/emulator.exe", "exe");
            WriteEntry(zip, "emulator/lib/a.dll", "dll");
        }
        string target = Path.Combine(_dir, "sdk", "emulator");

        PackageInstaller.ExtractFlattened(zipPath, target);

        Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "emulator.exe")));
        Assert.True(File.Exists(Path.Combine(target, "lib", "a.dll")));
        Assert.False(Directory.Exists(target + ".tmp"));
    }

    [Fact]
    public void Extract_blocks_path_traversal()
    {
        string zipPath = Path.Combine(_dir, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            WriteEntry(zip, "../../evil.txt", "x");

        Assert.Throws<SecurityException>(() =>
            PackageInstaller.ExtractFlattened(zipPath, Path.Combine(_dir, "sdk", "x")));
    }

    [Fact]
    public void Package_directory_maps_sdk_path()
    {
        var paths = new EmuDroid.Core.AppPaths(_dir);
        Assert.Equal(
            Path.Combine(_dir, "sdk", "system-images", "android-34", "google_apis_playstore", "x86_64"),
            paths.PackageDirectory("system-images;android-34;google_apis_playstore;x86_64"));
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(content);
    }
}
