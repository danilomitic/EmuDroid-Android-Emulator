namespace EmuDroid.Keymap;

public interface ITouchSink
{
    Task DownAsync(int id, double nx, double ny);
    Task UpAsync(int id, double nx, double ny);
}

/// <summary>
/// Turns keyboard and mouse input into touches according to a profile.
/// Pure logic: no Win32 here, so it can be unit tested.
/// </summary>
public sealed class KeymapEngine(ITouchSink sink, double aspect)
{
    public const string MouseLeft = "MouseLeft";
    public const string MouseRight = "MouseRight";

    private const int JoystickFinger = 1;
    private const int AimFinger = 2;
    private const int FireFinger = 3;
    private const int FirstTapFinger = 10;

    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    private KeymapProfile _profile = new();
    private bool _joystickActive;
    private (double X, double Y) _aimOffset;
    private bool _aimFingerDown;

    /// <summary>Screen height / width, used to keep joystick and aim circles round.</summary>
    public double Aspect { get; set; } = aspect;

    public bool AimMode { get; private set; }

    public KeymapProfile Profile
    {
        get => _profile;
        set
        {
            _ = ReleaseAllAsync();
            _profile = value;
        }
    }

    /// <summary>
    /// Quick synchronous check used by the input hook: true when this key/button is mapped right now
    /// and must not reach the emulator.
    /// </summary>
    public bool Handles(string key) =>
        IsMapped(key)
        || key.Equals(_profile.AimToggle, StringComparison.OrdinalIgnoreCase) && HasControl(ControlType.Aim)
        || AimMode && Is(key, MouseLeft) && HasControl(ControlType.Fire);

    /// <summary>Handles a key or mouse button. Returns true when the input was used and should not reach the emulator.</summary>
    public async Task<bool> OnDownAsync(string key)
    {
        if (!_down.Add(key)) return IsMapped(key); // auto-repeat

        if (key.Equals(_profile.AimToggle, StringComparison.OrdinalIgnoreCase) && HasControl(ControlType.Aim))
        {
            await SetAimModeAsync(!AimMode);
            return true;
        }

        bool used = false;
        for (int i = 0; i < _profile.Controls.Count; i++)
        {
            var c = _profile.Controls[i];
            switch (c.Type)
            {
                case ControlType.Tap when Is(key, c.Key):
                    await sink.DownAsync(FirstTapFinger + i, c.X, c.Y);
                    used = true;
                    break;
                case ControlType.Joystick when IsJoystickKey(c, key):
                    await UpdateJoystickAsync(c);
                    used = true;
                    break;
                case ControlType.Fire when AimMode && Is(key, MouseLeft):
                    await sink.DownAsync(FireFinger, c.X, c.Y);
                    used = true;
                    break;
            }
        }
        return used;
    }

    public async Task<bool> OnUpAsync(string key)
    {
        _down.Remove(key);
        bool used = false;
        for (int i = 0; i < _profile.Controls.Count; i++)
        {
            var c = _profile.Controls[i];
            switch (c.Type)
            {
                case ControlType.Tap when Is(key, c.Key):
                    await sink.UpAsync(FirstTapFinger + i, c.X, c.Y);
                    used = true;
                    break;
                case ControlType.Joystick when IsJoystickKey(c, key):
                    await UpdateJoystickAsync(c);
                    used = true;
                    break;
                case ControlType.Fire when Is(key, MouseLeft) && AimMode:
                    await sink.UpAsync(FireFinger, c.X, c.Y);
                    used = true;
                    break;
            }
        }
        return used || key.Equals(_profile.AimToggle, StringComparison.OrdinalIgnoreCase) && HasControl(ControlType.Aim);
    }

    /// <summary>Mouse movement in pixels while aim mode is on. Drags the aim finger; re-centers when it reaches the edge.</summary>
    public async Task OnMouseMoveAsync(double dxPixels, double dyPixels, double windowWidthPixels)
    {
        if (!AimMode || GetControl(ControlType.Aim) is not { } aim || windowWidthPixels <= 0) return;

        double scale = _profile.AimSensitivity / windowWidthPixels;
        _aimOffset = (_aimOffset.X + dxPixels * scale, _aimOffset.Y + dyPixels * scale / Math.Max(Aspect, 0.01));

        double r = aim.Radius;
        double ry = r / Math.Max(Aspect, 0.01);
        if (Math.Abs(_aimOffset.X) > r || Math.Abs(_aimOffset.Y) > ry)
        {
            // Finger reached the edge of the aim area: lift it and put it back in the middle, like a player would.
            await sink.UpAsync(AimFinger, aim.X + Math.Clamp(_aimOffset.X, -r, r), aim.Y + Math.Clamp(_aimOffset.Y, -ry, ry));
            _aimOffset = (0, 0);
            await sink.DownAsync(AimFinger, aim.X, aim.Y);
            return;
        }
        await sink.DownAsync(AimFinger, aim.X + _aimOffset.X, aim.Y + _aimOffset.Y);
    }

    public async Task SetAimModeAsync(bool on)
    {
        if (AimMode == on) return;
        AimMode = on;
        var aim = GetControl(ControlType.Aim);
        if (aim is null) return;
        if (on)
        {
            _aimOffset = (0, 0);
            await sink.DownAsync(AimFinger, aim.X, aim.Y);
            _aimFingerDown = true;
        }
        else if (_aimFingerDown)
        {
            await sink.UpAsync(AimFinger, aim.X + _aimOffset.X, aim.Y + _aimOffset.Y);
            _aimFingerDown = false;
        }
    }

    public async Task ReleaseAllAsync()
    {
        foreach (var key in _down.ToList()) await OnUpAsync(key);
        _down.Clear();
        await SetAimModeAsync(false);
    }

    /// <summary>Joystick finger position for the currently pressed direction keys, or null when none is pressed.</summary>
    public (double X, double Y)? JoystickPosition(KeyControl c)
    {
        double dx = (IsDown(c.Right) ? 1 : 0) - (IsDown(c.Left) ? 1 : 0);
        double dy = (IsDown(c.Down) ? 1 : 0) - (IsDown(c.Up) ? 1 : 0);
        if (dx == 0 && dy == 0) return null;
        double len = Math.Sqrt(dx * dx + dy * dy);
        return (c.X + dx / len * c.Radius, c.Y + dy / len * c.Radius / Math.Max(Aspect, 0.01));
    }

    private async Task UpdateJoystickAsync(KeyControl c)
    {
        var pos = JoystickPosition(c);
        if (pos is null)
        {
            if (_joystickActive) await sink.UpAsync(JoystickFinger, c.X, c.Y);
            _joystickActive = false;
            return;
        }
        if (!_joystickActive)
        {
            await sink.DownAsync(JoystickFinger, c.X, c.Y); // touch the center first, then slide
            _joystickActive = true;
        }
        await sink.DownAsync(JoystickFinger, pos.Value.X, pos.Value.Y);
    }

    private bool IsMapped(string key) =>
        _profile.Controls.Any(c => c.Type == ControlType.Tap && Is(key, c.Key) || c.Type == ControlType.Joystick && IsJoystickKey(c, key));

    private bool IsDown(string key) => _down.Contains(key);
    private bool HasControl(ControlType t) => _profile.Controls.Any(c => c.Type == t);
    private KeyControl? GetControl(ControlType t) => _profile.Controls.FirstOrDefault(c => c.Type == t);
    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool IsJoystickKey(KeyControl c, string key) => Is(key, c.Up) || Is(key, c.Left) || Is(key, c.Down) || Is(key, c.Right);
}
