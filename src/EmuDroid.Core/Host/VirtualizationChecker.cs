using System.ComponentModel;
using System.Diagnostics;
using System.Management;

namespace EmuDroid.Core.Host;

public enum FeatureState { Unknown, Enabled, Disabled }

/// <summary>Raw facts read from the PC. Kept separate from the decision so the decision can be unit tested.</summary>
public sealed record VirtualizationFacts(
    string CpuName,
    bool? FirmwareVirtualizationEnabled,
    bool HypervisorPresent,
    FeatureState WindowsHypervisorPlatform,
    double TotalRamGb,
    double FreeDiskGb);

public enum VirtualizationVerdict
{
    /// <summary>Everything is ready, the emulator will run fast.</summary>
    Ready,
    /// <summary>Windows Hypervisor Platform is off. We can turn it on (admin prompt + restart).</summary>
    NeedsWhpx,
    /// <summary>WHPX is on but Windows has not restarted since.</summary>
    NeedsRestart,
    /// <summary>VT-x / AMD-V is switched off in BIOS/UEFI. Only the user can fix this.</summary>
    BiosDisabled,
}

public sealed record SystemCheckResult(VirtualizationFacts Facts, VirtualizationVerdict Verdict, IReadOnlyList<string> Warnings)
{
    public const double MinRamGb = 8;
    public const double MinFreeDiskGb = 10;
    public bool CanRunEmulator => Verdict == VirtualizationVerdict.Ready;
}

public static class VirtualizationChecker
{
    public static SystemCheckResult Check(string installRoot)
    {
        var facts = ReadFacts(installRoot);
        return Evaluate(facts);
    }

    public static SystemCheckResult Evaluate(VirtualizationFacts f)
    {
        // When the Windows hypervisor is running, Win32_Processor reports firmware virtualization as "false"
        // because the hypervisor owns it. So a running hypervisor also proves virtualization is on in BIOS.
        bool firmwareOk = f.HypervisorPresent || f.FirmwareVirtualizationEnabled != false;

        var verdict = (firmwareOk, f.WindowsHypervisorPlatform, f.HypervisorPresent) switch
        {
            (false, _, _) => VirtualizationVerdict.BiosDisabled,
            (true, FeatureState.Disabled, _) => VirtualizationVerdict.NeedsWhpx,
            (true, FeatureState.Enabled, false) => VirtualizationVerdict.NeedsRestart,
            _ => VirtualizationVerdict.Ready,
        };

        var warnings = new List<string>();
        if (f.TotalRamGb > 0 && f.TotalRamGb < SystemCheckResult.MinRamGb)
            warnings.Add($"Your PC has {f.TotalRamGb:0.#} GB of RAM. At least {SystemCheckResult.MinRamGb} GB is recommended.");
        if (f.FreeDiskGb >= 0 && f.FreeDiskGb < SystemCheckResult.MinFreeDiskGb)
            warnings.Add($"Only {f.FreeDiskGb:0.#} GB of free disk space. EmuDroid needs at least {SystemCheckResult.MinFreeDiskGb} GB.");

        return new SystemCheckResult(f, verdict, warnings);
    }

    public static VirtualizationFacts ReadFacts(string installRoot)
    {
        string cpu = "Unknown CPU";
        bool? firmware = null;
        bool hypervisor = false;
        var whpx = FeatureState.Unknown;
        double ram = 0;

        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            foreach (ManagementObject o in s.Get())
            {
                cpu = (o["Name"] as string)?.Trim() ?? cpu;
                firmware = o["VirtualizationFirmwareEnabled"] as bool?;
                break;
            }
            using (var s = new ManagementObjectSearcher("SELECT HypervisorPresent, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in s.Get())
            {
                hypervisor = o["HypervisorPresent"] as bool? ?? false;
                ram = Convert.ToDouble(o["TotalPhysicalMemory"] ?? 0UL) / (1024d * 1024 * 1024);
                break;
            }
            using (var s = new ManagementObjectSearcher("SELECT InstallState FROM Win32_OptionalFeature WHERE Name='HypervisorPlatform'"))
            foreach (ManagementObject o in s.Get())
            {
                whpx = Convert.ToUInt32(o["InstallState"]) switch
                {
                    1 => FeatureState.Enabled,
                    2 => FeatureState.Disabled,
                    _ => FeatureState.Unknown,
                };
                break;
            }
        }
        catch (ManagementException) { /* WMI broken: keep Unknown values, the emulator will report problems itself. */ }

        double freeDisk = -1;
        try
        {
            freeDisk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(installRoot))!).AvailableFreeSpace / (1024d * 1024 * 1024);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }

        return new VirtualizationFacts(cpu, firmware, hypervisor, whpx, ram, freeDisk);
    }

    /// <summary>
    /// Turns on Windows Hypervisor Platform. Shows a UAC prompt. Returns true when a restart is needed.
    /// Throws <see cref="OperationCanceledException"/> if the user declines the admin prompt.
    /// </summary>
    public static async Task<bool> EnableWhpxAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "dism.exe"),
            Arguments = "/Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart /Quiet",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start DISM.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: user said no to UAC
        {
            throw new OperationCanceledException("Administrator permission was not granted.", e);
        }

        using (process)
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode switch
            {
                0 => false,
                3010 => true, // ERROR_SUCCESS_REBOOT_REQUIRED
                _ => throw new InvalidOperationException($"Windows could not enable Hypervisor Platform (DISM exit code {process.ExitCode})."),
            };
        }
    }

    public static void RestartWindows() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"Restarting to finish EmuDroid setup\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });

    /// <summary>Step-by-step guide shown when virtualization is off in BIOS/UEFI.</summary>
    public static string BiosGuide(string cpuName)
    {
        bool amd = cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        string setting = amd ? "SVM Mode (AMD-V)" : "Intel Virtualization Technology (VT-x)";
        return $"""
            Virtualization is turned off in your PC's BIOS/UEFI. Android needs it to run.

            1. Save your work and restart the PC.
            2. While it starts, press the BIOS key repeatedly. Common keys:
               ASUS: F2 or Del • MSI: Del • Gigabyte: Del • ASRock: F2 or Del
               Dell: F2 • HP: F10 (or Esc) • Lenovo: F1/F2 • Acer: F2
            3. Find "{setting}". It is usually under Advanced, CPU Configuration, or Overclocking/OC settings.
            4. Set it to Enabled.
            5. Save and exit (usually F10). Windows will start normally.
            6. Open EmuDroid again.

            Tip: In Windows 11 you can also reach BIOS from Settings → System → Recovery → Advanced startup → Restart now → Troubleshoot → Advanced options → UEFI Firmware Settings.
            """;
    }
}
