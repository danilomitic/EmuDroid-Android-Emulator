using System.Diagnostics;
using System.Text;

namespace EmuDroid.Core.Adb;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Combined => (Output + "\n" + Error).Trim();
}

/// <summary>Thin wrapper around adb.exe from Google's platform-tools.</summary>
public sealed class AdbClient(string adbExe)
{
    public string AdbExe { get; } = adbExe;

    public Task<CommandResult> RunAsync(string serial, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(AdbExe, ["-s", serial, .. args], timeout, ct);

    public async Task<bool> IsBootCompletedAsync(string serial, CancellationToken ct = default)
    {
        var r = await RunAsync(serial, ["shell", "getprop", "sys.boot_completed"], TimeSpan.FromSeconds(5), ct);
        return r.Success && r.Output.Trim() == "1";
    }

    public Task<CommandResult> InstallAsync(string serial, IReadOnlyList<string> apkFiles, CancellationToken ct = default)
    {
        string[] args = apkFiles.Count == 1
            ? ["install", "-r", "-g", apkFiles[0]]
            : ["install-multiple", "-r", "-g", .. apkFiles];
        return RunAsync(serial, args, TimeSpan.FromMinutes(5), ct);
    }

    public Task<CommandResult> KillEmulatorAsync(string serial, CancellationToken ct = default) =>
        RunAsync(serial, ["emu", "kill"], TimeSpan.FromSeconds(10), ct);

    /// <summary>Package name of the app currently in the foreground, or null.</summary>
    public async Task<string?> ForegroundPackageAsync(string serial, CancellationToken ct = default)
    {
        var r = await RunAsync(serial, ["shell", "dumpsys", "activity", "activities", "|", "grep", "-m", "1", "topResumedActivity"],
            TimeSpan.FromSeconds(5), ct);
        // Line looks like: topResumedActivity=ActivityRecord{abc u0 com.example.app/.MainActivity t12}
        foreach (var token in r.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = token.IndexOf('/');
            if (slash > 0 && token.Contains('.')) return token[..slash];
        }
        return null;
    }
}

public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default,
        IDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new CommandResult(-1, "", $"Timed out after {timeout.TotalSeconds:0} s.");
        }
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }
}
