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

    static readonly ushort[] TrackedModifiers =
    {
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x10, 0x11, 0x12
    };

    static HotkeyMonitor? _active;

    readonly object _gate = new();
    readonly HashSet<ushort> _swallowed = new();
    readonly HashSet<ushort> _down = new();
    readonly HashSet<ushort> _genuine = new();
    readonly HashSet<ushort> _logged = new();

    Native.HookProc? _proc;
    IntPtr _hook;
    DispatcherTimer? _watch;
    int _watchGeneration;
    bool _held;
    bool _sawNonModifier;
    ushort _modifierDown;

    public AppState? App { get; set; }
    public bool IsRunning { get; private set; }
    public string? Failure => null;

    public static void ReleaseStuckModifiers()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _active?.ReleaseStuck(force: false);
    }

    public bool Start()
    {
        Stop();
        _proc = Hook;
        _hook = Native.SetWindowsHookEx(Native.KeyboardHookId, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            _hook = Native.SetWindowsHookEx(Native.KeyboardHookId, _proc, IntPtr.Zero, 0);
        IsRunning = _hook != IntPtr.Zero;
        if (!IsRunning)
            return false;
        _active = this;
        ArmWatchdog();
        return true;
    }

    public void Stop()
    {
        StopWatchdog();
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
        IsRunning = false;
        var swallowed = false;
        lock (_gate)
            swallowed = _swallowed.Count > 0;
        if (swallowed)
            ReleaseStuck(force: true);
        _held = false;
        lock (_gate)
        {
            _swallowed.Clear();
            _down.Clear();
            _genuine.Clear();
            _logged.Clear();
        }
        if (_active == this)
            _active = null;
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
        var original = (ushort)data.VirtualKey;
        var key = Specific(original, data);
        if (App.IsCapturingHotkey)
        {
            NoteConsumed(original, key, isUp);
            Capture(key, isUp);
            if (isUp && IsModifierEvent(original, key))
                Dispatcher.UIThread.Post(() => ReleaseStuck(force: false));
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
                return Pass(code, wParam, lParam, original, key, isUp);
            if (!isUp)
            {
                NoteConsumed(original, key, false);
                if (!_held)
                {
                    _held = true;
                    Dispatcher.UIThread.Post(() => App.HotkeyPressed());
                }
                return 1;
            }
            if (_held)
            {
                _held = false;
                NoteConsumed(original, key, true);
                Dispatcher.UIThread.Post(() =>
                {
                    App.HotkeyReleased();
                    ReleaseStuck(force: false);
                });
                return 1;
            }
            return Pass(code, wParam, lParam, original, key, isUp);
        }

        if (key != hotkey.VirtualKey || CurrentMods() != hotkey.Modifiers)
            return Pass(code, wParam, lParam, original, key, isUp);
        if (!isUp)
        {
            if (_held)
                return 1;
            _held = true;
            NoteConsumed(original, key, false);
            Dispatcher.UIThread.Post(() => App.HotkeyPressed());
            return 1;
        }
        if (_held)
        {
            _held = false;
            NoteConsumed(original, key, true);
            Dispatcher.UIThread.Post(() =>
            {
                App.HotkeyReleased();
                ReleaseStuck(force: false);
            });
            return 1;
        }
        return Pass(code, wParam, lParam, original, key, isUp);
    }

    IntPtr Pass(int code, IntPtr wParam, IntPtr lParam, ushort original, ushort key, bool isUp)
    {
        NotePassed(original, key, isUp);
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

    void ArmWatchdog()
    {
        var generation = ++_watchGeneration;
        void StartTimer()
        {
            if (generation != _watchGeneration || !IsRunning)
                return;
            _watch?.Stop();
            _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _watch.Tick += (_, _) => OnWatch();
            _watch.Start();
        }
        if (Dispatcher.UIThread.CheckAccess())
            StartTimer();
        else
            Dispatcher.UIThread.Post(StartTimer);
    }

    void StopWatchdog()
    {
        _watchGeneration++;
        if (Dispatcher.UIThread.CheckAccess())
            StopTimer();
        else
            Dispatcher.UIThread.Post(StopTimer);
    }

    void StopTimer()
    {
        _watch?.Stop();
        _watch = null;
    }

    // Low-level hooks can leave a swallowed modifier logically down in Windows
    // when the key-up never reaches the OS. Compare our tracker with GetAsyncKeyState.
    void OnWatch()
    {
        if (!IsRunning || App == null)
            return;
        ForgetResolved();
        if (_held && !Native.KeyDown(App.Hotkey.VirtualKey))
        {
            var vk = App.Hotkey.VirtualKey;
            _held = false;
            MarkUp(vk);
            App.HotkeyReleased();
        }
        ReleaseStuck(force: false);
    }

    void NoteConsumed(ushort original, ushort specific, bool isUp)
    {
        if (!IsModifierEvent(original, specific))
            return;
        var outside = isUp ? (ushort)0 : OutsideAlreadyDown(specific, original);
        lock (_gate)
        {
            if (outside != 0 && !_down.Contains(outside) && !_swallowed.Contains(outside))
                _genuine.Add(outside);
            foreach (var vk in ModifierGroup(original, specific))
            {
                _genuine.Remove(vk);
                _swallowed.Add(vk);
                if (isUp)
                    _down.Remove(vk);
                else
                    _down.Add(vk);
            }
        }
    }

    void NotePassed(ushort original, ushort key, bool isUp)
    {
        if (!IsModifierEvent(original, key))
            return;
        lock (_gate)
        {
            foreach (var vk in ModifierGroup(original, key))
            {
                if (IsGeneric(vk) && OwnsOtherSide(vk, key))
                    continue;
                if (isUp)
                {
                    _genuine.Remove(vk);
                    continue;
                }
                _swallowed.Remove(vk);
                _down.Remove(vk);
                _genuine.Add(vk);
            }
        }
    }

    bool OwnsOtherSide(ushort generic, ushort passing)
    {
        var (left, right) = Sides(generic);
        var leftOwned = left != 0 && left != passing && _swallowed.Contains(left);
        var rightOwned = right != 0 && right != passing && _swallowed.Contains(right);
        return leftOwned || rightOwned;
    }

    static ushort OutsideAlreadyDown(ushort specific, ushort original)
    {
        var vk = Hotkey.IsModifierKey(specific) ? specific : original;
        var other = OtherSide(vk);
        return other != 0 && Native.KeyDown(other) ? other : (ushort)0;
    }

    void MarkUp(ushort vk)
    {
        lock (_gate)
        {
            _down.Remove(vk);
            var generic = GenericOf(vk);
            if (generic != 0)
                _down.Remove(generic);
        }
    }

    void ReleaseStuck(bool force)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var keys = KeysToRelease(force);
        if (keys.Count == 0)
            return;
        var released = new List<ushort>(keys.Count);
        foreach (var vk in keys)
        {
            lock (_gate)
            {
                if (!force && (Busy() || _down.Contains(vk)))
                    continue;
                if (!_swallowed.Contains(vk) && _genuine.Contains(vk))
                    continue;
            }
            if (!Native.KeyDown(vk))
                continue;
            if (Native.ReleaseVirtualKey(vk))
                released.Add(vk);
        }
        LogReleased(released);
    }

    List<ushort> KeysToRelease(bool force)
    {
        lock (_gate)
        {
            if (!force && Busy())
                return new List<ushort>();

            var result = new List<ushort>();
            foreach (var vk in TrackedModifiers)
            {
                if (!IsSwallowedStuck(vk, force))
                    continue;
                if (IsGeneric(vk) && ExplainedByUnconsumedSide(vk, force))
                    continue;
                result.Add(vk);
            }

            if (!AltFamilyStuck(force))
                return result;

            foreach (var vk in new ushort[] { 0xA4, 0xA5, 0x12 })
            {
                if (result.Contains(vk) || !LogicallyUp(vk, force) || !Native.KeyDown(vk) || _genuine.Contains(vk))
                    continue;
                if (vk == 0x12)
                {
                    if (!ExplainedByUnconsumedSide(0x12, force))
                        result.Add(vk);
                    continue;
                }
                if (_swallowed.Contains(vk) || SiblingAltStuck(vk, force))
                    result.Add(vk);
            }
            return result;
        }
    }

    bool Busy() => _held || App?.IsCapturingHotkey == true;

    bool LogicallyUp(ushort vk, bool force) => force || !_down.Contains(vk);

    bool AltFamilyStuck(bool force) =>
        IsSwallowedStuck(0xA4, force) || IsSwallowedStuck(0xA5, force)
        || (IsSwallowedStuck(0x12, force) && !ExplainedByUnconsumedSide(0x12, force));

    bool SiblingAltStuck(ushort vk, bool force) =>
        vk == 0xA4 ? IsSwallowedStuck(0xA5, force) : vk == 0xA5 && IsSwallowedStuck(0xA4, force);

    bool IsSwallowedStuck(ushort vk, bool force) =>
        _swallowed.Contains(vk) && LogicallyUp(vk, force) && Native.KeyDown(vk);

    bool ExplainedByUnconsumedSide(ushort generic, bool force)
    {
        var (left, right) = Sides(generic);
        if (left == 0)
            return false;
        if (GenuineDown(left) || GenuineDown(right))
            return true;
        if (IsSwallowedStuck(left, force) || IsSwallowedStuck(right, force))
            return false;
        return UnconsumedDown(left) || UnconsumedDown(right);
    }

    bool GenuineDown(ushort vk) => vk != 0 && _genuine.Contains(vk) && Native.KeyDown(vk);

    bool UnconsumedDown(ushort vk) =>
        vk != 0 && !_swallowed.Contains(vk) && !_down.Contains(vk) && Native.KeyDown(vk);

    void ForgetResolved()
    {
        lock (_gate)
            DropUp(_logged);
        lock (_gate)
            DropUp(_genuine);
    }

    void DropUp(HashSet<ushort> keys)
    {
        if (keys.Count == 0)
            return;
        List<ushort>? up = null;
        foreach (var vk in keys)
        {
            if (!Native.KeyDown(vk))
                (up ??= new List<ushort>()).Add(vk);
        }
        if (up == null)
            return;
        foreach (var vk in up)
            keys.Remove(vk);
    }

    void LogReleased(List<ushort> released)
    {
        if (released.Count == 0)
            return;
        var fresh = new List<ushort>();
        lock (_gate)
        {
            foreach (var vk in released)
            {
                if (_logged.Add(vk))
                    fresh.Add(vk);
            }
        }
        if (fresh.Count == 0)
            return;
        AppLog.Shared.Write("INFO", "Отпущен застрявший модификатор: " + string.Join(", ", fresh.Select(Label)));
    }

    static bool IsModifierEvent(ushort original, ushort key) =>
        Hotkey.IsModifierKey(original) || Hotkey.IsModifierKey(key);

    static IEnumerable<ushort> ModifierGroup(ushort original, ushort specific)
    {
        if (Hotkey.IsModifierKey(specific))
            yield return specific;
        if (original != specific && Hotkey.IsModifierKey(original))
            yield return original;
        var generic = GenericOf(specific);
        if (generic != 0)
            yield return generic;
        generic = GenericOf(original);
        if (generic != 0 && generic != specific && generic != original)
            yield return generic;
    }

    static ushort GenericOf(ushort vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => 0
    };

    static (ushort Left, ushort Right) Sides(ushort generic) => generic switch
    {
        0x10 => ((ushort)0xA0, (ushort)0xA1),
        0x11 => ((ushort)0xA2, (ushort)0xA3),
        0x12 => ((ushort)0xA4, (ushort)0xA5),
        _ => ((ushort)0, (ushort)0)
    };

    static bool IsGeneric(ushort vk) => vk is 0x10 or 0x11 or 0x12;

    static ushort OtherSide(ushort vk) => vk switch
    {
        0xA0 => 0xA1,
        0xA1 => 0xA0,
        0xA2 => 0xA3,
        0xA3 => 0xA2,
        0xA4 => 0xA5,
        0xA5 => 0xA4,
        0x5B => 0x5C,
        0x5C => 0x5B,
        _ => 0
    };

    static string Label(ushort vk) => vk switch
    {
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        _ => Hotkey.ModifierName(vk) ?? ("0x" + vk.ToString("X2"))
    };
}
