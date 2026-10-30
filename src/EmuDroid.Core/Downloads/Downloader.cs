using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace EmuDroid.Core.Downloads;

public sealed record DownloadProgress(long BytesDone, long TotalBytes, double BytesPerSecond)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesDone / TotalBytes, 0, 1) : 0;

    public TimeSpan? Eta => BytesPerSecond > 1 && TotalBytes > BytesDone
        ? TimeSpan.FromSeconds((TotalBytes - BytesDone) / BytesPerSecond)
        : null;
}

public sealed class ChecksumMismatchException(string file) : Exception(
    $"The downloaded file '{Path.GetFileName(file)}' is damaged (checksum mismatch). Please try again.");

/// <summary>
/// Downloads a file with resume support. Data goes to "&lt;target&gt;.part" and is renamed only after the SHA-1 matches.
/// </summary>
public sealed class Downloader(HttpClient http)
{
    private const int BufferSize = 1 << 16;

    public async Task DownloadAsync(
        Uri url, string targetFile, string expectedSha1, long expectedSize,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(targetFile) && await HashMatchesAsync(targetFile, expectedSha1, ct))
        {
            progress?.Report(new DownloadProgress(expectedSize, expectedSize, 0));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        string partFile = targetFile + ".part";
        long existing = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(partFile);
            existing = 0;
        }

        if (existing < expectedSize || expectedSize <= 0)
            await DownloadToPartAsync(url, partFile, existing, expectedSize, progress, ct);

        if (!await HashMatchesAsync(partFile, expectedSha1, ct))
        {
            File.Delete(partFile);
            throw new ChecksumMismatchException(targetFile);
        }

        File.Move(partFile, targetFile, overwrite: true);
    }

    private async Task DownloadToPartAsync(
        Uri url, string partFile, long existing, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            existing = 0; // Server ignored the range request: start over.
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return; // Nothing left to download; the checksum decides if the file is good.
        response.EnsureSuccessStatusCode();

        long total = expectedSize > 0
            ? expectedSize
            : existing + (response.Content.Headers.ContentLength ?? 0);

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partFile, existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long done = existing;
        var clock = Stopwatch.StartNew();
        long windowStartBytes = done;
        var windowStart = clock.Elapsed;
        double speed = 0;
        var lastReport = TimeSpan.Zero;

        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            var now = clock.Elapsed;
            if ((now - windowStart).TotalSeconds >= 1)
            {
                speed = (done - windowStartBytes) / (now - windowStart).TotalSeconds;
                windowStart = now;
                windowStartBytes = done;
            }
            if ((now - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = now;
                progress?.Report(new DownloadProgress(done, total, speed));
            }
        }
        progress?.Report(new DownloadProgress(done, total, speed));
    }

    public static async Task<bool> HashMatchesAsync(string file, string expectedSha1, CancellationToken ct = default)
    {
        if (!File.Exists(file)) return false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        byte[] hash = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }
}
