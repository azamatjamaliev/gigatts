using Avalonia.Threading;

namespace GigaTTS;

interface IHotkeySource
{
    AppState? App { get; set; }
    bool IsRunning { get; }
    string? Failure { get; }
    bool Start();
    void Stop();
    void ResetCapture();
}

static class HotkeySources
{
    public static IHotkeySource Create() =>
        OperatingSystem.IsWindows() ? CreateWindows() : CreateLinux();

    static IHotkeySource CreateWindows() => new HotkeyMonitor();

    static IHotkeySource CreateLinux() => new LinuxHotkeyMonitor();
}

sealed class HotkeyMonitor : IHotkeySource
{
    const uint Injected = 0x10;
    const uint LowerInjected = 0x02;
    const uint Extended = 0x01;
    const uint KeyUp = 0x80;

    Native.HookProc? _proc;
    IntPtr _hook;
    bool _held;
    bool _sawNonModifier;
    ushort _modifierDown;

    public AppState? App { get; set; }
    public bool IsRunning { get; private set; }
    public string? Failure => null;

    public bool Start()
    {
        Stop();
        _proc = Hook;
        _hook = Native.SetWindowsHookEx(Native.KeyboardHookId, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            _hook = Native.SetWindowsHookEx(Native.KeyboardHookId, _proc, IntPtr.Zero, 0);
        IsRunning = _hook != IntPtr.Zero;
        return IsRunning;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
        IsRunning = false;
        _held = false;
    }

    public void ResetCapture()
    {
        _sawNonModifier = false;
        _modifierDown = 0;
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || App == null)
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KeyboardHook>(lParam);
        if ((data.Flags & (Injected | LowerInjected)) != 0)
            return Native.CallNextHookEx(_hook, code, wParam, lParam);

        var isUp = (data.Flags & KeyUp) != 0;
        var key = Specific((ushort)data.VirtualKey, data);
        if (App.IsCapturingHotkey)
        {
            Capture(key, isUp);
            return 1;
        }

        if (!isUp && key == 0x1B && App.ShouldCancelWithEscape)
        {
            Dispatcher.UIThread.Post(() => App.CancelDictation());
            return 1;
        }

        var hotkey = App.Hotkey;
        if (hotkey.IsModifier)
        {
            if (key != hotkey.VirtualKey)
                return Native.CallNextHookEx(_hook, code, wParam, lParam);
            if (!isUp && !_held)
            {
                _held = true;
                Dispatcher.UIThread.Post(() => App.HotkeyPressed());
                return 1;
            }
            if (isUp && _held)
            {
                _held = false;
                Dispatcher.UIThread.Post(() => App.HotkeyReleased());
                return 1;
            }
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (key != hotkey.VirtualKey || CurrentMods() != hotkey.Modifiers)
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        if (!isUp)
        {
            if (_held)
                return 1;
            _held = true;
            Dispatcher.UIThread.Post(() => App.HotkeyPressed());
            return 1;
        }
        if (_held)
        {
            _held = false;
            Dispatcher.UIThread.Post(() => App.HotkeyReleased());
            return 1;
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Capture(ushort key, bool isUp)
    {
        if (!isUp && key == 0x1B)
        {
            Dispatcher.UIThread.Post(() => App?.CancelHotkeyCapture());
            return;
        }
        if (!isUp && !Hotkey.IsModifierKey(key))
        {
            _sawNonModifier = true;
            var mods = CurrentMods();
            Dispatcher.UIThread.Post(() => App?.ApplyHotkey(new Hotkey(key, false, mods)));
            return;
        }
        if (!Hotkey.IsModifierKey(key))
            return;
        if (!isUp)
        {
            _modifierDown = key;
            return;
        }
        if (!_sawNonModifier && _modifierDown == key)
            Dispatcher.UIThread.Post(() => App?.ApplyHotkey(new Hotkey(key, true)));
    }

    static ushort Specific(ushort key, Native.KeyboardHook data) => Specific(key, data.ScanCode, data.Flags);

    static ushort Specific(ushort key, uint scanCode, uint flags = 0)
    {
        var extended = (flags & Extended) != 0;
        return key switch
        {
            0x10 => scanCode == 0x36 ? (ushort)0xA1 : (ushort)0xA0,
            0x11 => extended ? (ushort)0xA3 : (ushort)0xA2,
            0x12 => extended ? (ushort)0xA5 : (ushort)0xA4,
            _ => key
        };
    }

    static KeyMods CurrentMods()
    {
        var mods = KeyMods.None;
        if (Native.KeyDown(0x11) || Native.KeyDown(0xA2) || Native.KeyDown(0xA3))
            mods |= KeyMods.Control;
        if (Native.KeyDown(0x12) || Native.KeyDown(0xA4) || Native.KeyDown(0xA5))
            mods |= KeyMods.Alt;
        if (Native.KeyDown(0x10) || Native.KeyDown(0xA0) || Native.KeyDown(0xA1))
            mods |= KeyMods.Shift;
        if (Native.KeyDown(0x5B) || Native.KeyDown(0x5C))
            mods |= KeyMods.Win;
        return mods;
    }
}
