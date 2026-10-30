using EmuDroid.Core.Host;

namespace EmuDroid.Tests;

public class VirtualizationTests
{
    private static VirtualizationFacts Facts(bool? firmware = true, bool hypervisor = true,
        FeatureState whpx = FeatureState.Enabled, double ram = 16, double disk = 100) =>
        new("AMD Ryzen 5 2600X", firmware, hypervisor, whpx, ram, disk);

    [Fact]
    public void Ready_when_whpx_on_and_hypervisor_running() =>
        Assert.Equal(VirtualizationVerdict.Ready, VirtualizationChecker.Evaluate(Facts()).Verdict);

    [Fact]
    public void Hypervisor_running_overrides_false_firmware_flag() =>
        // Windows reports firmware virtualization as false while its own hypervisor is running.
        Assert.Equal(VirtualizationVerdict.Ready, VirtualizationChecker.Evaluate(Facts(firmware: false)).Verdict);

    [Fact]
    public void Needs_whpx_when_feature_disabled() =>
        Assert.Equal(VirtualizationVerdict.NeedsWhpx,
            VirtualizationChecker.Evaluate(Facts(hypervisor: false, whpx: FeatureState.Disabled)).Verdict);

    [Fact]
    public void Needs_restart_when_enabled_but_hypervisor_not_running() =>
        Assert.Equal(VirtualizationVerdict.NeedsRestart,
            VirtualizationChecker.Evaluate(Facts(hypervisor: false)).Verdict);

    [Fact]
    public void Bios_disabled_wins_over_everything() =>
        Assert.Equal(VirtualizationVerdict.BiosDisabled,
            VirtualizationChecker.Evaluate(Facts(firmware: false, hypervisor: false, whpx: FeatureState.Disabled)).Verdict);

    [Fact]
    public void Warns_about_low_ram_and_disk()
    {
        var r = VirtualizationChecker.Evaluate(Facts(ram: 4, disk: 3));
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void Bios_guide_names_the_right_setting()
    {
        Assert.Contains("SVM", VirtualizationChecker.BiosGuide("AMD Ryzen 5 2600X"));
        Assert.Contains("VT-x", VirtualizationChecker.BiosGuide("Intel(R) Core(TM) i7"));
    }

    [Fact]
    public void Reads_real_facts_from_this_pc()
    {
        var facts = VirtualizationChecker.ReadFacts(Path.GetTempPath());
        Assert.NotEqual("Unknown CPU", facts.CpuName);
        Assert.True(facts.TotalRamGb > 0);
    }
}
