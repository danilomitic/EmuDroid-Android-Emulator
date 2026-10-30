using System.Windows;
using System.Windows.Threading;
using EmuDroid.Core;
using EmuDroid.Core.Adb;

namespace EmuDroid.Keymap;

/// <summary>
/// Keyboard &amp; mouse controls for one running instance: overlay window + input hook + touch sender.
/// Play mode: keys become touches while the emulator window is in front. Edit mode: place controls on screen.
/// </summary>
public sealed class KeymapSession : IDisposable
{
    private readonly string _profilesFolder;
    private readonly string _avdId;
    private readonly int _consolePort;
    private readonly string _serial;
    private readonly AdbClient _adb;
    private readonly TouchSender _sender;
    private readonly KeymapEngine _engine;
    private readonly InputHook _hook = new();
    private readonly OverlayWindow _overlay;
    private readonly DispatcherTimer _tracker;
    private readonly string _toggleKey;
    private IntPtr _emulatorWindow;
    private string? _foregroundPackage;
    private int _ticks;
    private bool _enabled = true;

    public KeymapSession(string profilesFolder, string avdId, int consolePort, int grpcPort, int screenWidth, int screenHeight,
        AdbClient adb, string toggleKey = "F12")
    {
        _profilesFolder = profilesFolder;
        _avdId = avdId;
        _consolePort = consolePort;
        _serial = $"emulator-{consolePort}";
        _adb = adb;
        _toggleKey = toggleKey;
        _sender = new TouchSender(grpcPort, screenWidth, screenHeight);
        _engine = new KeymapEngine(_sender, (double)screenHeight / screenWidth)
        {
            Profile = KeymapProfile.Load(profilesFolder, null),
        };

        _overlay = new OverlayWindow(this);
        _tracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tracker.Tick += async (_, _) => await TrackAsync();

        _hook.IsActive = () => _emulatorWindow != IntPtr.Zero && Native.GetForegroundWindow() == _emulatorWindow && !_overlay.IsEditing;
        _hook.OnKey = OnKey;
        _hook.OnMouseButton = OnMouseButton;
        _hook.OnMouseMove = OnMouseMove;
    }

    public event EventHandler? Closed;

    public KeymapProfile Profile => _engine.Profile;
    public string? ForegroundPackage => _foregroundPackage;
    public bool Enabled => _enabled;

    public void Start()
    {
        _hook.Install();
        _tracker.Start();
        _ = TrackAsync();
        _overlay.Show();
    }

    public void Edit() => _overlay.BeginEdit();

    /// <summary>Saves the profile for the app in front (or as the default profile when no app is known).</summary>
    public void SaveProfile(KeymapProfile profile, bool forThisAppOnly)
    {
        profile.PackageName = forThisAppOnly && _foregroundPackage is not null ? _foregroundPackage : KeymapProfile.DefaultName;
        profile.Save(_profilesFolder);
        _engine.Profile = profile;
        Log.Info($"Keymap saved for {profile.PackageName} ({profile.Controls.Count} controls)");
    }

    private bool OnKey(string key, bool down)
    {
        if (down && key == _toggleKey)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _enabled = !_enabled;
                if (!_enabled) _ = _engine.ReleaseAllAsync();
                _overlay.Refresh();
            });
            return true;
        }
        if (!_enabled || !_engine.Handles(key)) return false;
        _ = Run(() => down ? _engine.OnDownAsync(key) : _engine.OnUpAsync(key));
        return true;
    }

    private bool OnMouseButton(string button, bool down)
    {
        if (!_enabled || !_engine.Handles(button)) return false;
        _ = Run(() => down ? _engine.OnDownAsync(button) : _engine.OnUpAsync(button));
        if (button == KeymapEngine.MouseRight && down)
            Application.Current.Dispatcher.BeginInvoke(() => SetCursorHidden(_engine.AimMode));
        return true;
    }

    private bool OnMouseMove(int x, int y)
    {
        if (!_enabled || !_engine.AimMode) return false;
        var r = Native.ClientScreenRect(_emulatorWindow);
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        int dx = x - cx, dy = y - cy;
        if (dx == 0 && dy == 0) return false;
        Native.SetCursorPos(cx, cy); // keep the cursor parked in the middle, like a game does
        _ = Run(() => _engine.OnMouseMoveAsync(dx, dy, r.Width));
        return true;
    }

    private bool _cursorHidden;

    private void SetCursorHidden(bool hidden)
    {
        if (hidden == _cursorHidden) return;
        _cursorHidden = hidden;
        Native.ShowCursor(!hidden);
        if (hidden && _emulatorWindow != IntPtr.Zero)
        {
            var r = Native.ClientScreenRect(_emulatorWindow);
            Native.SetCursorPos(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }
    }

    private static async Task<bool> Run(Func<Task<bool>> action)
    {
        try { return await action(); }
        catch (Exception e) { Log.Error("Keymap input failed", e); return false; }
    }

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Log.Error("Keymap input failed", e); }
    }

    /// <summary>Keeps the overlay glued to the emulator window and loads the profile of the app in front.</summary>
    private async Task TrackAsync()
    {
        if (_emulatorWindow == IntPtr.Zero || _ticks % 8 == 0 && !Native.IsWindowVisible(_emulatorWindow))
            _emulatorWindow = Native.FindEmulatorWindow(_consolePort, _avdId);

        if (_emulatorWindow == IntPtr.Zero || Native.IsIconic(_emulatorWindow))
        {
            _overlay.Hide();
        }
        else
        {
            var r = Native.ClientScreenRect(_emulatorWindow);
            _overlay.PlaceOver(r.Left, r.Top, r.Width, r.Height);
            bool emulatorInFront = Native.GetForegroundWindow() == _emulatorWindow || _overlay.IsEditing || _overlay.IsActive;
            if (emulatorInFront && !_overlay.IsVisible) _overlay.Show();
            if (!emulatorInFront && _overlay.IsVisible && !_overlay.IsEditing) _overlay.Hide();
        }

        if (_engine.AimMode && Native.GetForegroundWindow() != _emulatorWindow)
        {
            await _engine.SetAimModeAsync(false);
            SetCursorHidden(false);
        }

        // Every 3 seconds: which app is in front? Load its profile.
        if (_ticks++ % 12 == 0 && !_overlay.IsEditing)
        {
            try
            {
                string? package = await _adb.ForegroundPackageAsync(_serial);
                if (package is not null && package != _foregroundPackage)
                {
                    _foregroundPackage = package;
                    _engine.Profile = KeymapProfile.Load(_profilesFolder, package);
                    _overlay.Refresh();
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Close()
    {
        _overlay.Close();
    }

    internal void OnOverlayClosed()
    {
        Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _tracker.Stop();
        _hook.Dispose();
        SetCursorHidden(false);
        _ = _engine.ReleaseAllAsync();
        _sender.Dispose();
    }
}
