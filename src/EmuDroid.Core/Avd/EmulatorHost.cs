using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EmuDroid.Core.Adb;

namespace EmuDroid.Core.Avd;

public enum InstanceState { Stopped, Starting, Booting, Running, Stopping }

public sealed class RunningInstance
{
    public required InstanceConfig Config { get; init; }
    public required int ConsolePort { get; init; }
    public required int GrpcPort { get; init; }
    public required Process Process { get; init; }
    public InstanceState State { get; internal set; } = InstanceState.Starting;
    public string Serial => $"emulator-{ConsolePort}";
    public int AdbPort => ConsolePort + 1;
}

public sealed class InstanceStateChangedEventArgs(string instanceId, InstanceState state, string? error) : EventArgs
{
    public string InstanceId { get; } = instanceId;
    public InstanceState State { get; } = state;
    public string? Error { get; } = error;
}

/// <summary>Starts, tracks and stops emulator processes. Each instance gets its own port pair and gRPC port.</summary>
public sealed class EmulatorHost(AppPaths paths, AdbClient adb) : IDisposable
{
    public const int FirstConsolePort = 5554;
    public const int LastConsolePort = 5682;
    public const int FirstGrpcPort = 8554;

    private readonly ConcurrentDictionary<string, RunningInstance> _running = new();
    private readonly CancellationTokenSource _shutdown = new();

    public event EventHandler<InstanceStateChangedEventArgs>? StateChanged;

    public IReadOnlyCollection<RunningInstance> Running => _running.Values.ToList();

    public RunningInstance? Get(string id) => _running.GetValueOrDefault(id);

    public InstanceState StateOf(string id) => _running.TryGetValue(id, out var r) ? r.State : InstanceState.Stopped;

    public RunningInstance Start(InstanceConfig cfg, bool coldBoot = false)
    {
        if (_running.ContainsKey(cfg.Id))
            throw new InvalidOperationException($"\"{cfg.DisplayName}\" is already running.");
        if (!File.Exists(paths.EmulatorExe))
            throw new FileNotFoundException("The Android Emulator is not installed yet.", paths.EmulatorExe);

        var (console, grpc) = AllocatePorts(_running.Values.Select(r => r.ConsolePort).ToHashSet(), IsPortFree);

        var psi = new ProcessStartInfo(paths.EmulatorExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(paths.EmulatorExe)!,
        };
        foreach (var arg in BuildArguments(cfg, console, grpc, coldBoot)) psi.ArgumentList.Add(arg);
        foreach (var (k, v) in Environment(paths)) psi.Environment[k] = v;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the emulator.");
        var instance = new RunningInstance { Config = cfg, ConsolePort = console, GrpcPort = grpc, Process = process };
        _running[cfg.Id] = instance;

        string log = Path.Combine(paths.Logs, $"emulator-{cfg.Id}.log");
        Directory.CreateDirectory(paths.Logs);
        var writer = new StreamWriter(log, append: false) { AutoFlush = true };
        var lastLines = new ConcurrentQueue<string>();
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (writer) writer.WriteLine(e.Data);
            lastLines.Enqueue(e.Data);
            while (lastLines.Count > 30) lastLines.TryDequeue(out string? _);
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (writer) writer.Dispose();
            bool wasStopping = instance.State == InstanceState.Stopping;
            _running.TryRemove(cfg.Id, out RunningInstance? _);
            string? error = null;
            if (!wasStopping && process.ExitCode != 0)
                error = ExplainFailure(lastLines.ToArray(), process.ExitCode);
            SetState(instance, InstanceState.Stopped, error);
        };

        SetState(instance, InstanceState.Booting, null);
        _ = WatchBootAsync(instance, _shutdown.Token);
        return instance;
    }

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        if (!_running.TryGetValue(id, out var instance)) return;
        SetState(instance, InstanceState.Stopping, null);

        await adb.KillEmulatorAsync(instance.Serial, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await instance.Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { instance.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    public static IReadOnlyList<string> BuildArguments(InstanceConfig cfg, int consolePort, int grpcPort, bool coldBoot)
    {
        var args = new List<string>
        {
            "-avd", cfg.Id,
            "-port", consolePort.ToString(),
            "-grpc", grpcPort.ToString(),
            "-gpu", AvdManager.GpuValue(cfg.Gpu),
            "-no-boot-anim",
        };
        if (coldBoot) args.Add("-no-snapshot-load");
        return args;
    }

    public static IReadOnlyDictionary<string, string> Environment(AppPaths paths) => new Dictionary<string, string>
    {
        ["ANDROID_SDK_ROOT"] = paths.Sdk,
        ["ANDROID_HOME"] = paths.Sdk,
        ["ANDROID_AVD_HOME"] = paths.Avd,
        ["ANDROID_USER_HOME"] = Path.Combine(paths.Root, "android-user"),
        ["ANDROID_EMULATOR_HOME"] = Path.Combine(paths.Root, "android-user"),
    };

    /// <summary>Picks the first free even console port (adb uses port+1) and a matching gRPC port.</summary>
    public static (int Console, int Grpc) AllocatePorts(IReadOnlySet<int> usedByUs, Func<int, bool> isFree)
    {
        for (int port = FirstConsolePort; port <= LastConsolePort; port += 2)
        {
            if (usedByUs.Contains(port)) continue;
            int grpc = FirstGrpcPort + (port - FirstConsolePort) / 2;
            if (isFree(port) && isFree(port + 1) && isFree(grpc))
                return (port, grpc);
        }
        throw new InvalidOperationException("Too many instances are running. Stop one and try again.");
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Turns typical emulator error output into a message a non-technical user understands.</summary>
    public static string ExplainFailure(IReadOnlyList<string> lastLines, int exitCode)
    {
        string text = string.Join('\n', lastLines);
        if (text.Contains("WHPX", StringComparison.OrdinalIgnoreCase) || text.Contains("hypervisor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("acceleration", StringComparison.OrdinalIgnoreCase))
            return "Android could not start because hardware acceleration is not available. Open Settings → System check and turn on Windows Hypervisor Platform.";
        if (text.Contains("Not enough memory", StringComparison.OrdinalIgnoreCase) || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            return "Not enough free memory to start this instance. Close other programs or lower its RAM in instance settings.";
        if (text.Contains("Running multiple emulators with the same AVD", StringComparison.OrdinalIgnoreCase))
            return "This instance is already running in another window.";
        if (text.Contains("system image", StringComparison.OrdinalIgnoreCase) || text.Contains("Cannot find AVD system path", StringComparison.OrdinalIgnoreCase))
            return "The Android system image for this instance is missing. Download it again in Settings → Android versions.";
        return $"Android stopped unexpectedly (code {exitCode}). See the log in Settings → Logs for details.";
    }

    private async Task WatchBootAsync(RunningInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!ct.IsCancellationRequested && !instance.Process.HasExited && DateTime.UtcNow < deadline)
        {
            try
            {
                if (await adb.IsBootCompletedAsync(instance.Serial, ct))
                {
                    SetState(instance, InstanceState.Running, null);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetState(RunningInstance instance, InstanceState state, string? error)
    {
        if (instance.State == InstanceState.Stopping && state is InstanceState.Running or InstanceState.Booting) return;
        instance.State = state;
        StateChanged?.Invoke(this, new InstanceStateChangedEventArgs(instance.Config.Id, state, error));
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
