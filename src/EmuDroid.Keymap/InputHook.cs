using System.Runtime.InteropServices;
using System.Windows.Input;

namespace EmuDroid.Keymap;

/// <summary>
/// Low-level keyboard and mouse hooks. They only act while the emulator window is in front,
/// so typing in any other program is never affected.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private readonly Native.HookProc _keyboardProc;
    private readonly Native.HookProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    /// <summary>Return true to swallow the key. Arguments: key name, isDown.</summary>
    public Func<string, bool, bool>? OnKey { get; set; }
    /// <summary>Return true to swallow the button. Arguments: button name, isDown.</summary>
    public Func<string, bool, bool>? OnMouseButton { get; set; }
    /// <summary>Return true to swallow the move. Arguments: screen x, y.</summary>
    public Func<int, int, bool>? OnMouseMove { get; set; }
    /// <summary>Only handle input when this returns true (emulator window in front).</summary>
    public Func<bool> IsActive { get; set; } = () => false;

    public InputHook()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        IntPtr module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"Could not watch the keyboard and mouse (error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && OnKey is not null)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            if ((down || up) && IsActive())
            {
                string key = KeyInterop.KeyFromVirtualKey((int)data.vkCode).ToString();
                if (OnKey(key, down)) return 1;
            }
        }
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & Native.LLMHF_INJECTED) != 0;
            if (!injected && IsActive())
            {
                bool swallow = msg switch
                {
                    Native.WM_MOUSEMOVE => OnMouseMove?.Invoke(data.pt.X, data.pt.Y) ?? false,
                    Native.WM_LBUTTONDOWN => OnMouseButton?.Invoke(KeymapEngine.MouseLeft, true) ?? false,
                    Native.WM_LBUTTONUP => OnMouseButton?.Invoke(KeymapEngine.MouseLeft, false) ?? false,
                    Native.WM_RBUTTONDOWN => OnMouseButton?.Invoke(KeymapEngine.MouseRight, true) ?? false,
                    Native.WM_RBUTTONUP => OnMouseButton?.Invoke(KeymapEngine.MouseRight, false) ?? false,
                    _ => false,
                };
                if (swallow) return 1;
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }
}
