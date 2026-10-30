using EmuDroid.Core;
using EmuDroid.Core.Avd;

namespace EmuDroid.Tests;

public sealed class AvdTests : IDisposable
{
    private const string Image = "system-images;android-34;google_apis_playstore;x86_64";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "emudroid-avd-" + Guid.NewGuid().ToString("N"));
    private readonly AvdManager _avd;

    public AvdTests() => _avd = new AvdManager(new AppPaths(_dir));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Theory]
    [InlineData("My Phone #2", "My_Phone_2")]
    [InlineData("  ", "Instance")]
    [InlineData("Igrice čćž", "Igrice")]
    [InlineData("a/b\\c", "a_b_c")]
    public void Sanitizes_ids(string name, string expected) => Assert.Equal(expected, AvdManager.SanitizeId(name));

    [Fact]
    public void Create_writes_avd_files_and_lists_instance()
    {
        var cfg = _avd.Create("Main", 34, Image);

        string config = File.ReadAllText(Path.Combine(_avd.AvdDirectory("Main"), "config.ini"));
        Assert.Contains("image.sysdir.1=system-images/android-34/google_apis_playstore/x86_64/\n", config);
        Assert.Contains("PlayStore.enabled=true\n", config);
        Assert.Contains("tag.id=google_apis_playstore\n", config);
        Assert.Contains("hw.keyboard=yes\n", config);
        Assert.Contains("hw.ramSize=4096\n", config);

        string top = File.ReadAllText(Path.Combine(_dir, "avd", "Main.ini"));
        Assert.Contains($"path={_avd.AvdDirectory("Main")}", top);
        Assert.Contains("target=android-34", top);

        Assert.Equal(cfg, Assert.Single(_avd.List()));
    }

    [Fact]
    public void Duplicate_names_get_unique_ids()
    {
        var a = _avd.Create("Game", 34, Image);
        var b = _avd.Create("Game", 34, Image);
        Assert.Equal("Game", a.Id);
        Assert.Equal("Game_2", b.Id);
    }

    [Fact]
    public void Clone_copies_data_and_drops_locks()
    {
        var src = _avd.Create("Main", 34, Image);
        File.WriteAllText(Path.Combine(_avd.AvdDirectory(src.Id), "userdata-qemu.img"), "data");
        File.WriteAllText(Path.Combine(_avd.AvdDirectory(src.Id), "hardware-qemu.ini.lock"), "");

        var clone = _avd.Clone(src, "Main copy");

        Assert.Equal("data", File.ReadAllText(Path.Combine(_avd.AvdDirectory(clone.Id), "userdata-qemu.img")));
        Assert.False(File.Exists(Path.Combine(_avd.AvdDirectory(clone.Id), "hardware-qemu.ini.lock")));
        Assert.Contains("AvdId=Main_copy\n", File.ReadAllText(Path.Combine(_avd.AvdDirectory(clone.Id), "config.ini")));
        Assert.Equal(2, _avd.List().Count);
    }

    [Fact]
    public void Rename_keeps_id_and_wipe_removes_user_data()
    {
        var cfg = _avd.Create("Main", 34, Image);
        string dir = _avd.AvdDirectory(cfg.Id);
        File.WriteAllText(Path.Combine(dir, "userdata-qemu.img"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "snapshots", "default_boot"));

        var renamed = _avd.Rename(cfg, "Work");
        _avd.WipeData(renamed);

        Assert.Equal("Main", renamed.Id);
        Assert.Equal("Work", _avd.List().Single().DisplayName);
        Assert.False(File.Exists(Path.Combine(dir, "userdata-qemu.img")));
        Assert.False(Directory.Exists(Path.Combine(dir, "snapshots")));
        Assert.True(File.Exists(Path.Combine(dir, "config.ini")));
    }

    [Fact]
    public void Delete_removes_everything()
    {
        var cfg = _avd.Create("Main", 34, Image);
        _avd.Delete(cfg);
        Assert.Empty(_avd.List());
        Assert.False(File.Exists(Path.Combine(_dir, "avd", "Main.ini")));
    }

    [Fact]
    public void Ports_skip_used_and_busy_ones()
    {
        var (c1, g1) = EmulatorHost.AllocatePorts(new HashSet<int>(), _ => true);
        Assert.Equal((5554, 8554), (c1, g1));

        var (c2, g2) = EmulatorHost.AllocatePorts(new HashSet<int> { 5554 }, p => p != 5557);
        Assert.Equal((5558, 8556), (c2, g2));
    }

    [Fact]
    public void Arguments_include_ports_and_gpu()
    {
        var cfg = new InstanceConfig { Id = "Main", DisplayName = "Main", ApiLevel = 34, SystemImage = Image, Gpu = GpuMode.Software };
        var args = EmulatorHost.BuildArguments(cfg, 5556, 8555, coldBoot: true);
        Assert.Equal(["-avd", "Main", "-port", "5556", "-grpc", "8555", "-gpu", "swiftshader_indirect", "-no-boot-anim", "-no-snapshot-load"], args);
    }

    [Fact]
    public void Explains_acceleration_failure_in_plain_words()
    {
        string msg = EmulatorHost.ExplainFailure(["ERROR | x86_64 emulation currently requires hardware acceleration!"], 1);
        Assert.Contains("Windows Hypervisor Platform", msg);
    }
}
