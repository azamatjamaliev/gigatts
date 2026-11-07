using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace GigaTTS;

sealed class LinuxHotkeyMonitor : IHotkeySource
{
    const string X11Failure = "Не удалось включить глобальную клавишу в сессии X11. Проверьте libx11-6 и что клавишу не заняла другая программа. Диктовка из меню доступна.";
    const string WaylandFailure = "Сессия Wayland не отдаёт глобальные клавиши. Добавьте пользователя в группу input и войдите заново: sudo usermod -aG input $USER. Либо запустите сеанс X11. Диктовка из меню работает без этой клавиши.";

    readonly HotkeyLogic _logic = new();
    Thread? _thread;
    volatile bool _running;
    volatile bool _cancel;
    string? _failure;

    public AppState? App { get; set; }
    public bool IsRunning { get; private set; }
    public string? Failure => _failure;

    public bool Start()
    {
        Stop();
        _cancel = false;
        _failure = null;
        var session = LinuxSession.Kind;
        AppLog.Shared.Write("INFO", "Сессия ввода: " + session);
        if (session == "wayland")
        {
            if (StartEvdev())
                return true;
            _failure = WaylandFailure;
            return false;
        }
        if (session == "x11")
        {
            if (StartX11())
                return true;
            var x11Failure = string.IsNullOrEmpty(_failure) ? X11Failure : _failure;
            if (StartEvdev())
            {
                _failure = null;
                return true;
            }
            _failure = x11Failure;
            return false;
        }
        _failure = "Глобальная клавиша доступна в графической сессии. Сейчас сессия: " + session + ". Диктовка включается из меню.";
        IsRunning = false;
        return false;
    }

    public void Stop()
    {
        _cancel = true;
        _running = false;
        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            thread.Join(2000);
        IsRunning = false;
        _logic.Reset();
    }

    public void ResetCapture() => _logic.ResetCapture();

    bool StartX11()
    {
        _cancel = false;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => RunX11(ready))
        {
            IsBackground = true,
            Name = "GigaTTS.Hotkey"
        };
        _thread.Start();
        ready.Wait(8000);
        if (!_running)
        {
            Stop();
            if (string.IsNullOrEmpty(_failure))
                _failure = X11Failure;
            return false;
        }
        IsRunning = true;
        AppLog.Shared.Write("INFO", "Глобальная клавиша X11 включена. Правый Alt перехватывается.");
        return true;
    }

    bool StartEvdev()
    {
        _cancel = false;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => RunEvdev(ready))
        {
            IsBackground = true,
            Name = "GigaTTS.Hotkey"
        };
        _thread.Start();
        ready.Wait(2000);
        if (!_running)
        {
            Stop();
            if (string.IsNullOrEmpty(_failure))
                _failure = "Не удалось читать клавиатуру (/dev/input). Диктовка доступна из меню.";
            return false;
        }
        IsRunning = true;
        if (LinuxSession.IsWayland)
            AppLog.Shared.Write("WARN", "Сессия Wayland: клавиша читается, но не проглатывается и может сработать как Alt. На X11 правый Alt перехватывается полностью.");
        return true;
    }

    void RunX11(ManualResetEventSlim ready)
    {
        var display = IntPtr.Zero;
        try
        {
            display = X11.Open();
            if (display == IntPtr.Zero)
            {
                _failure = "Не удалось открыть дисплей X11. " + X11Failure;
                ready.Set();
                return;
            }
            var root = X11.DefaultRoot(display);
            if (!X11.GrabHotkey(display, root, App?.Hotkey ?? Hotkey.RightAlt, primaryOnly: true))
            {
                _failure = X11Failure;
                X11.Close(display);
                display = IntPtr.Zero;
                ready.Set();
                return;
            }
            if (_cancel)
            {
                ready.Set();
                return;
            }
            _running = true;
            ready.Set();
            var signature = "";
            while (_running)
            {
                var next = Signature();
                if (next != signature)
                {
                    signature = next;
                    _logic.Reset();
                    X11.Rebind(display, root, App);
                }
                while (_running && X11.Pending(display) > 0)
                    Dispatch(display);
                Thread.Sleep(20);
            }
        }
        catch (DllNotFoundException)
        {
            _failure = "Не найдена libX11. Установите пакет libx11-6. " + X11Failure;
            _running = false;
            try { ready.Set(); } catch { /* Already signaled. */ }
        }
        catch (Exception ex)
        {
            _failure = "Глобальная клавиша X11 остановилась: " + ex.Message;
            _running = false;
            IsRunning = false;
            AppLog.Shared.Write("ERROR", _failure);
            try { ready.Set(); } catch { /* Already signaled. */ }
        }
        finally
        {
            if (display != IntPtr.Zero)
                X11.Close(display);
        }
    }

    void Dispatch(IntPtr display)
    {
        var ev = new X11.XEvent();
        X11.Next(display, ref ev);
        if (ev.Type is not (2 or 3) || App == null)
            return;
        var vk = X11.VirtualKey(display, ev.KeyCode);
        if (vk == 0)
            return;
        _logic.OnKey(App, vk, isUp: ev.Type == 3);
    }

    void RunEvdev(ManualResetEventSlim ready)
    {
        var fds = new List<int>();
        var denied = 0;
        try
        {
            fds = Evdev.OpenKeyboards(ref denied);
            if (fds.Count == 0)
            {
                _failure = denied > 0
                    ? "Нет доступа к /dev/input. Для Wayland добавьте пользователя в группу input и войдите заново."
                    : "Клавиатура в /dev/input не найдена. Диктовка доступна из меню.";
                ready.Set();
                return;
            }
            if (_cancel)
            {
                ready.Set();
                return;
            }
            _running = true;
            ready.Set();
            AppLog.Shared.Write("INFO", "Глобальная клавиша читает /dev/input (" + fds.Count + ").");
            var poll = fds.Select(fd => new Evdev.PollFd { Fd = fd, Events = 1 }).ToArray();
            var buffer = new byte[24];
            while (_running)
            {
                var rc = Evdev.Poll(poll, (nuint)poll.Length, 200);
                if (rc == 0 || (rc < 0 && Marshal.GetLastWin32Error() == 4))
                    continue;
                if (rc < 0)
                    break;
                for (var index = 0; index < poll.Length; index++)
                {
                    if ((poll[index].REvents & 1) == 0)
                        continue;
                    while (_running)
                    {
                        var read = Evdev.Read(poll[index].Fd, buffer, 24);
                        if (read != 24)
                            break;
                        HandleEvdev(buffer);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _failure = "Чтение клавиатуры остановилось: " + ex.Message;
            _running = false;
            IsRunning = false;
            AppLog.Shared.Write("ERROR", _failure);
            try { ready.Set(); } catch { /* Already signaled. */ }
        }
        finally
        {
            foreach (var fd in fds)
                Evdev.Close(fd);
        }
    }

    void HandleEvdev(byte[] buffer)
    {
        if (App == null)
            return;
        var type = BitConverter.ToUInt16(buffer, 16);
        var code = BitConverter.ToUInt16(buffer, 18);
        var value = BitConverter.ToInt32(buffer, 20);
        if (type != 1 || value == 2)
            return;
        var vk = LinuxKeys.FromEvdev(code);
        if (vk == 0)
            return;
        _logic.OnKey(App, vk, isUp: value == 0);
    }

    string Signature()
    {
        var hotkey = App?.Hotkey;
        if (hotkey == null)
            return "";
        var capture = App!.IsCapturingHotkey ? "1" : "0";
        var escape = App.ShouldCancelWithEscape ? "1" : "0";
        return $"{hotkey.VirtualKey}:{hotkey.IsModifier}:{(uint)hotkey.Modifiers}:{capture}:{escape}";
    }
}

sealed class HotkeyLogic
{
    readonly HashSet<ushort> _down = new();
    bool _held;
    bool _sawNonModifier;
    ushort _modifierDown;

    public void Reset()
    {
        _held = false;
        _sawNonModifier = false;
        _modifierDown = 0;
        _down.Clear();
    }

    public void ResetCapture()
    {
        _sawNonModifier = false;
        _modifierDown = 0;
    }

    public void OnKey(AppState app, ushort key, bool isUp)
    {
        if (key == 0)
            return;
        if (Hotkey.IsModifierKey(key))
        {
            if (isUp)
                _down.Remove(key);
            else
                _down.Add(key);
        }

        if (app.IsCapturingHotkey)
        {
            Capture(app, key, isUp);
            return;
        }

        if (!isUp && key == 0x1B && app.ShouldCancelWithEscape)
        {
            Dispatcher.UIThread.Post(() => app.CancelDictation());
            return;
        }

        var hotkey = app.Hotkey;
        if (hotkey.IsModifier)
        {
            if (key != hotkey.VirtualKey)
                return;
            if (!isUp && !_held)
            {
                _held = true;
                Dispatcher.UIThread.Post(() => app.HotkeyPressed());
                return;
            }
            if (isUp && _held)
            {
                _held = false;
                Dispatcher.UIThread.Post(() => app.HotkeyReleased());
            }
            return;
        }

        if (!isUp)
        {
            if (key != hotkey.VirtualKey || CurrentMods() != hotkey.Modifiers || _held)
                return;
            _held = true;
            Dispatcher.UIThread.Post(() => app.HotkeyPressed());
            return;
        }
        if (key == hotkey.VirtualKey && _held)
        {
            _held = false;
            Dispatcher.UIThread.Post(() => app.HotkeyReleased());
        }
    }

    void Capture(AppState app, ushort key, bool isUp)
    {
        if (!isUp && key == 0x1B)
        {
            Dispatcher.UIThread.Post(() => app.CancelHotkeyCapture());
            return;
        }
        if (!isUp && !Hotkey.IsModifierKey(key))
        {
            _sawNonModifier = true;
            var mods = CurrentMods();
            Dispatcher.UIThread.Post(() => app.ApplyHotkey(new Hotkey(key, false, mods)));
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
            Dispatcher.UIThread.Post(() => app.ApplyHotkey(new Hotkey(key, true)));
    }

    KeyMods CurrentMods()
    {
        var mods = KeyMods.None;
        if (_down.Contains(0x11) || _down.Contains(0xA2) || _down.Contains(0xA3))
            mods |= KeyMods.Control;
        if (_down.Contains(0x12) || _down.Contains(0xA4) || _down.Contains(0xA5))
            mods |= KeyMods.Alt;
        if (_down.Contains(0x10) || _down.Contains(0xA0) || _down.Contains(0xA1))
            mods |= KeyMods.Shift;
        if (_down.Contains(0x5B) || _down.Contains(0x5C))
            mods |= KeyMods.Win;
        return mods;
    }
}

static class LinuxKeys
{
    static readonly Dictionary<int, ushort> Evdev = BuildEvdev();

    public static ushort FromEvdev(int code) => Evdev.TryGetValue(code, out var vk) ? vk : (ushort)0;

    public static ushort FromKeysym(ulong keysym)
    {
        if (keysym is >= 0x61 and <= 0x7A)
            return (ushort)(keysym - 0x20);
        if (keysym is >= 0x30 and <= 0x39)
            return (ushort)keysym;
        if (keysym is >= 0xFFBE and <= 0xFFC9)
            return (ushort)(0x70 + (keysym - 0xFFBE));
        return keysym switch
        {
            0xFFE9 => 0xA4,
            0xFFEA or 0xFE03 => 0xA5,
            0xFFE1 => 0xA0,
            0xFFE2 => 0xA1,
            0xFFE3 => 0xA2,
            0xFFE4 => 0xA3,
            0xFFEB or 0xFFE7 => 0x5B,
            0xFFEC or 0xFFE8 => 0x5C,
            0xFF1B => 0x1B,
            0x0020 => 0x20,
            0xFF0D => 0x0D,
            0xFF09 => 0x09,
            0xFF08 => 0x08,
            0xFFFF => 0x2E,
            0xFF51 => 0x25,
            0xFF52 => 0x26,
            0xFF53 => 0x27,
            0xFF54 => 0x28,
            _ => 0
        };
    }

    public static IEnumerable<string> Names(ushort vk)
    {
        switch (vk)
        {
            case 0xA5:
                yield return "Alt_R";
                yield return "ISO_Level3_Shift";
                yield break;
            case 0xA4: yield return "Alt_L"; yield break;
            case 0xA0: yield return "Shift_L"; yield break;
            case 0xA1: yield return "Shift_R"; yield break;
            case 0xA2: yield return "Control_L"; yield break;
            case 0xA3: yield return "Control_R"; yield break;
            case 0x5B: yield return "Super_L"; yield break;
            case 0x5C: yield return "Super_R"; yield break;
            case 0x1B: yield return "Escape"; yield break;
            case 0x20: yield return "space"; yield break;
            case 0x0D: yield return "Return"; yield break;
            case 0x09: yield return "Tab"; yield break;
            case 0x08: yield return "BackSpace"; yield break;
            case 0x2E: yield return "Delete"; yield break;
            case 0x25: yield return "Left"; yield break;
            case 0x26: yield return "Up"; yield break;
            case 0x27: yield return "Right"; yield break;
            case 0x28: yield return "Down"; yield break;
        }
        if (vk is >= 0x41 and <= 0x5A)
        {
            yield return ((char)(vk + 32)).ToString();
            yield break;
        }
        if (vk is >= 0x30 and <= 0x39)
        {
            yield return ((char)vk).ToString();
            yield break;
        }
        if (vk is >= 0x70 and <= 0x7B)
            yield return "F" + (vk - 0x6F);
    }

    static Dictionary<int, ushort> BuildEvdev()
    {
        var map = new Dictionary<int, ushort>
        {
            [1] = 0x1B,
            [14] = 0x08,
            [15] = 0x09,
            [28] = 0x0D,
            [29] = 0xA2,
            [42] = 0xA0,
            [54] = 0xA1,
            [56] = 0xA4,
            [57] = 0x20,
            [97] = 0xA3,
            [100] = 0xA5,
            [103] = 0x26,
            [105] = 0x25,
            [106] = 0x27,
            [108] = 0x28,
            [111] = 0x2E,
            [125] = 0x5B,
            [126] = 0x5C,
            [87] = 0x7A,
            [88] = 0x7B
        };
        MapRow(map, "qwertyuiop", 16);
        MapRow(map, "asdfghjkl", 30);
        MapRow(map, "zxcvbnm", 44);
        for (var index = 0; index < 9; index++)
            map[2 + index] = (ushort)('1' + index);
        map[11] = (ushort)'0';
        for (var index = 0; index < 10; index++)
            map[59 + index] = (ushort)(0x70 + index);
        return map;
    }

    static void MapRow(Dictionary<int, ushort> map, string row, int origin)
    {
        for (var index = 0; index < row.Length; index++)
            map[origin + index] = (ushort)char.ToUpperInvariant(row[index]);
    }
}

static class X11
{
    const uint Shift = 1;
    const uint Lock = 2;
    const uint Control = 4;
    const uint Mod1 = 8;
    const uint Mod2 = 16;
    const uint Mod4 = 64;
    const uint AnyModifier = 1u << 15;
    const int Async = 1;

    static readonly XErrorHandler ErrorHandler = OnError;
    static IntPtr _display;
    static bool _error;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    // LP64 XKeyEvent: type at 0, state at 80, keycode at 84. The buffer is larger than XEvent (192).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint KeyCode;
    }

    [DllImport("libX11.so.6")]
    static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern int XNextEvent(IntPtr display, ref XEvent ev);

    [DllImport("libX11.so.6")]
    static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern int XSync(IntPtr display, int discard);

    [DllImport("libX11.so.6")]
    static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

    [DllImport("libX11.so.6")]
    static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport("libX11.so.6")]
    static extern int XGrabKeyboard(IntPtr display, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode, IntPtr time);

    [DllImport("libX11.so.6")]
    static extern int XUngrabKeyboard(IntPtr display, IntPtr time);

    [DllImport("libX11.so.6")]
    static extern ulong XStringToKeysym(string name);

    [DllImport("libX11.so.6")]
    static extern int XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport("libX11.so.6")]
    static extern ulong XKeycodeToKeysym(IntPtr display, int keycode, int index);

    [DllImport("libX11.so.6", EntryPoint = "XSetErrorHandler")]
    static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    [DllImport("libX11.so.6", EntryPoint = "XSetErrorHandler")]
    static extern IntPtr XSetErrorHandlerRaw(IntPtr handler);

    public static IntPtr Open()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        _display = display;
        return display;
    }

    public static void Close(IntPtr display)
    {
        var previous = IntPtr.Zero;
        try
        {
            previous = XSetErrorHandler(ErrorHandler);
            var root = XDefaultRootWindow(display);
            XUngrabKey(display, 0, AnyModifier, root);
            XUngrabKeyboard(display, IntPtr.Zero);
            XFlush(display);
            XCloseDisplay(display);
        }
        catch
        {
            // The display is already gone.
        }
        finally
        {
            XSetErrorHandlerRaw(previous);
        }
        if (_display == display)
            _display = IntPtr.Zero;
    }

    public static IntPtr DefaultRoot(IntPtr display) => XDefaultRootWindow(display);

    public static int Pending(IntPtr display) => XPending(display);

    public static void Next(IntPtr display, ref XEvent ev) => XNextEvent(display, ref ev);

    public static ushort VirtualKey(IntPtr display, uint keycode)
    {
        for (var level = 0; level < 4; level++)
        {
            var keysym = XKeycodeToKeysym(display, (int)keycode, level);
            var vk = LinuxKeys.FromKeysym(keysym);
            if (vk != 0)
                return vk;
        }
        return 0;
    }

    public static bool GrabHotkey(IntPtr display, IntPtr root, Hotkey hotkey, bool primaryOnly)
    {
        var codes = Keycodes(display, hotkey.VirtualKey);
        if (codes.Count == 0)
            return false;
        var primary = hotkey.IsModifier ? LockVariants(0) : LockVariants(ModifierMask(hotkey.Modifiers));
        var ok = Grab(display, root, codes, primary, syncEach: true);
        if (!primaryOnly && hotkey.IsModifier)
            Grab(display, root, codes, ExtraCombos(), syncEach: false);
        return ok;
    }

    public static void Rebind(IntPtr display, IntPtr root, AppState? app)
    {
        var previous = XSetErrorHandler(ErrorHandler);
        try
        {
            _error = false;
            XUngrabKey(display, 0, AnyModifier, root);
            XUngrabKeyboard(display, IntPtr.Zero);
            XSync(display, 0);
            if (app?.IsCapturingHotkey == true && XGrabKeyboard(display, root, 0, Async, Async, IntPtr.Zero) == 0)
            {
                XFlush(display);
                return;
            }
            if (app?.Hotkey is { } hotkey)
                GrabHotkey(display, root, hotkey, primaryOnly: false);
            if (app?.ShouldCancelWithEscape == true && app.Hotkey.VirtualKey != 0x1B)
                Grab(display, root, Keycodes(display, 0x1B), LockVariants(0), syncEach: false);
            XFlush(display);
        }
        finally
        {
            XSetErrorHandlerRaw(previous);
        }
    }

    static bool Grab(IntPtr display, IntPtr root, List<int> codes, IEnumerable<uint> masks, bool syncEach)
    {
        var previous = XSetErrorHandler(ErrorHandler);
        var any = false;
        try
        {
            foreach (var code in codes)
            {
                foreach (var mask in masks)
                {
                    _error = false;
                    XGrabKey(display, code, mask, root, 0, Async, Async);
                    if (syncEach)
                    {
                        XSync(display, 0);
                        if (!_error)
                            any = true;
                    }
                    else
                    {
                        any = true;
                    }
                }
            }
            if (!syncEach)
                XSync(display, 0);
        }
        finally
        {
            XSetErrorHandlerRaw(previous);
        }
        return any;
    }

    static List<int> Keycodes(IntPtr display, ushort vk)
    {
        var result = new List<int>();
        foreach (var name in LinuxKeys.Names(vk))
        {
            var keysym = XStringToKeysym(name);
            if (keysym == 0)
                continue;
            var code = XKeysymToKeycode(display, keysym);
            if (code > 0 && !result.Contains(code))
                result.Add(code);
        }
        return result;
    }

    static uint ModifierMask(KeyMods mods)
    {
        uint mask = 0;
        if (mods.HasFlag(KeyMods.Shift))
            mask |= Shift;
        if (mods.HasFlag(KeyMods.Control))
            mask |= Control;
        if (mods.HasFlag(KeyMods.Alt))
            mask |= Mod1;
        if (mods.HasFlag(KeyMods.Win))
            mask |= Mod4;
        return mask;
    }

    static uint[] LockVariants(uint mask) => new[]
    {
        mask,
        mask | Lock,
        mask | Mod2,
        mask | Lock | Mod2
    };

    static IEnumerable<uint> ExtraCombos()
    {
        uint[] bits = { Shift, Control, Mod1, Mod4 };
        for (var index = 1; index < 16; index++)
        {
            uint mask = 0;
            for (var bit = 0; bit < 4; bit++)
            {
                if ((index & (1 << bit)) != 0)
                    mask |= bits[bit];
            }
            foreach (var variant in LockVariants(mask))
                yield return variant;
        }
    }

    static int OnError(IntPtr display, IntPtr errorEvent)
    {
        if (display == _display)
            _error = true;
        return 0;
    }
}

static class Evdev
{
    const int ORdOnly = 0;
    const int ONonBlock = 0x800;
    const int OCloExec = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int read(int fd, byte[] buffer, nuint count);

    [DllImport("libc")]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);

    public static int Read(int fd, byte[] buffer, nuint count) => read(fd, buffer, count);

    public static int Poll(PollFd[] fds, nuint count, int timeout) => poll(fds, count, timeout);

    public static void Close(int fd) => close(fd);

    public static List<int> OpenKeyboards(ref int denied)
    {
        var opened = new List<int>();
        var fallback = new List<int>();
        string[] paths;
        try
        {
            paths = Directory.GetFiles("/dev/input", "event*");
        }
        catch
        {
            return opened;
        }
        Array.Sort(paths, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var fd = open(path, ORdOnly | ONonBlock | OCloExec);
            if (fd < 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 1 or 13)
                    denied++;
                continue;
            }
            if (LooksLikeKeyboard(path))
                opened.Add(fd);
            else
                fallback.Add(fd);
        }
        if (opened.Count > 0)
        {
            foreach (var fd in fallback)
                close(fd);
            return opened;
        }
        return fallback;
    }

    static bool LooksLikeKeyboard(string path)
    {
        var leaf = Path.GetFileName(path);
        var file = "/sys/class/input/" + leaf + "/device/capabilities/key";
        try
        {
            if (!File.Exists(file))
                return true;
            var text = File.ReadAllText(file);
            return HasBit(text, 30) || HasBit(text, 1) || HasBit(text, 28);
        }
        catch
        {
            return true;
        }
    }

    static bool HasBit(string text, int code)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        Array.Reverse(parts);
        var wordIndex = code / 64;
        var bit = code % 64;
        if (wordIndex >= parts.Length)
            return false;
        if (!ulong.TryParse(parts[wordIndex], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var word))
            return false;
        return (word & (1UL << bit)) != 0;
    }
}
