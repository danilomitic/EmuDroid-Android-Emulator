using EmuDroid.Keymap;

namespace EmuDroid.Tests;

public class KeymapEngineTests
{
    private sealed class Recorder : ITouchSink
    {
        public List<string> Events { get; } = [];
        public Task DownAsync(int id, double nx, double ny) { Events.Add($"down {id} {nx:0.00} {ny:0.00}"); return Task.CompletedTask; }
        public Task UpAsync(int id, double nx, double ny) { Events.Add($"up {id} {nx:0.00} {ny:0.00}"); return Task.CompletedTask; }
    }

    private static (KeymapEngine Engine, Recorder Sink) Create(params KeyControl[] controls)
    {
        var sink = new Recorder();
        var engine = new KeymapEngine(sink, aspect: 1.0) { Profile = new KeymapProfile { Controls = [.. controls] } };
        return (engine, sink);
    }

    [Fact]
    public async Task Tap_presses_and_releases_with_the_key()
    {
        var (e, s) = Create(new KeyControl { Type = ControlType.Tap, Key = "Space", X = 0.8, Y = 0.7 });

        Assert.True(await e.OnDownAsync("Space"));
        Assert.True(await e.OnDownAsync("Space")); // auto-repeat: no second touch
        Assert.True(await e.OnUpAsync("Space"));

        Assert.Equal(["down 10 0.80 0.70", "up 10 0.80 0.70"], s.Events);
    }

    [Fact]
    public async Task Unmapped_keys_pass_through()
    {
        var (e, s) = Create(new KeyControl { Type = ControlType.Tap, Key = "Space", X = 0.5, Y = 0.5 });
        Assert.False(await e.OnDownAsync("Q"));
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Joystick_moves_finger_diagonally_and_lifts_when_released()
    {
        var (e, s) = Create(new KeyControl { Type = ControlType.Joystick, X = 0.2, Y = 0.7, Radius = 0.1 });

        await e.OnDownAsync("W");
        await e.OnDownAsync("D");
        await e.OnUpAsync("W");
        await e.OnUpAsync("D");

        Assert.Equal(
        [
            "down 1 0.20 0.70", // touch center
            "down 1 0.20 0.60", // up
            "down 1 0.27 0.63", // up-right (normalized diagonal)
            "down 1 0.30 0.70", // right
            "up 1 0.20 0.70",
        ], s.Events);
    }

    [Fact]
    public async Task Aim_mode_drags_finger_and_fire_only_works_in_aim_mode()
    {
        var (e, s) = Create(
            new KeyControl { Type = ControlType.Aim, X = 0.6, Y = 0.5, Radius = 0.1 },
            new KeyControl { Type = ControlType.Fire, X = 0.9, Y = 0.8 });

        Assert.False(await e.OnDownAsync(KeymapEngine.MouseLeft)); // not aiming: normal click
        await e.OnUpAsync(KeymapEngine.MouseLeft);

        await e.OnDownAsync(KeymapEngine.MouseRight); // toggle aim on
        await e.OnUpAsync(KeymapEngine.MouseRight);
        await e.OnMouseMoveAsync(50, 0, windowWidthPixels: 1000);
        Assert.True(await e.OnDownAsync(KeymapEngine.MouseLeft));
        await e.OnUpAsync(KeymapEngine.MouseLeft);

        Assert.Equal(
        [
            "down 2 0.60 0.50",
            "down 2 0.65 0.50",
            "down 3 0.90 0.80",
            "up 3 0.90 0.80",
        ], s.Events);
    }

    [Fact]
    public async Task Aim_recenters_at_the_edge()
    {
        var (e, s) = Create(new KeyControl { Type = ControlType.Aim, X = 0.5, Y = 0.5, Radius = 0.1 });
        await e.OnDownAsync(KeymapEngine.MouseRight);

        await e.OnMouseMoveAsync(150, 0, 1000); // 0.15 > radius

        Assert.Equal(["down 2 0.50 0.50", "up 2 0.60 0.50", "down 2 0.50 0.50"], s.Events);
    }

    [Fact]
    public void Profiles_round_trip_and_fall_back_to_default()
    {
        string dir = Path.Combine(Path.GetTempPath(), "emudroid-keymap-" + Guid.NewGuid().ToString("N"));
        try
        {
            new KeymapProfile { PackageName = "default", Controls = [new KeyControl { Type = ControlType.Tap, Key = "E", X = 0.1, Y = 0.2 }] }.Save(dir);

            var p = KeymapProfile.Load(dir, "com.some.game");

            Assert.Equal("default", p.PackageName);
            Assert.Equal("E", Assert.Single(p.Controls).Key);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
