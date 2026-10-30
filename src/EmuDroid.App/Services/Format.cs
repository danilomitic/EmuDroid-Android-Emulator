using EmuDroid.Core.Downloads;
using EmuDroid.Core.Sdk;

namespace EmuDroid.App.Services;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static string Progress(InstallProgress p)
    {
        if (p.Download is not { } d) return p.Stage + "…";
        string text = $"{p.Stage} — {Bytes(d.BytesDone)} of {Bytes(d.TotalBytes)}";
        if (d.BytesPerSecond > 0) text += $" at {Bytes((long)d.BytesPerSecond)}/s";
        if (d.Eta is { } eta) text += eta.TotalMinutes >= 1 ? $", about {Math.Ceiling(eta.TotalMinutes):0} min left" : ", less than a minute left";
        return text;
    }

    /// <summary>Short, user-facing message for an exception.</summary>
    public static string Error(Exception e) => e switch
    {
        System.Net.Http.HttpRequestException => "Could not reach Google's download servers. Check your internet connection and try again.",
        TaskCanceledException or OperationCanceledException => "The operation was cancelled.",
        IOException io when io.HResult == unchecked((int)0x80070070) => "Your disk is full. Free up some space and try again.",
        _ => e.Message,
    };
}
