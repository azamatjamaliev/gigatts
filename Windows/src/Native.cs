using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace GigaTTS;

static class Native
{
    const int WhKeyboardLl = 13;
    const uint GmemMoveable = 0x0002;
    const uint CfUnicodeText = 13;
    const uint KeyeventfExtendedKey = 0x0001;
    const uint KeyeventfKeyup = 0x0002;
    const uint KeyeventfUnicode = 0x0004;
    const uint MapvkVkToVsc = 0;
    const int InputKeyboard = 1;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHook
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr memory);

    public static int KeyboardHookId => WhKeyboardLl;

    public static PixelPoint Cursor()
    {
        return GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : new PixelPoint(0, 0);
    }

    public static bool KeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static void MakeClickThrough(IntPtr window)
    {
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero)
            return;
        const int styleIndex = -20;
        const long layered = 0x00080000;
        const long transparent = 0x20;
        const long tool = 0x80;
        const long noActivate = 0x08000000;
        var updated = GetStyle(window, styleIndex) | layered | transparent | tool | noActivate;
        SetStyle(window, styleIndex, updated);
    }

    public static bool IsPasswordFocused()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info))
            return false;
        return HasPasswordStyle(info.Focus) || HasPasswordStyle(foreground);
    }

    public static bool SendText(string text)
    {
        var events = new List<Input>(text.Length * 2);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    continue;
                events.Add(VirtualKey(0x0D, false));
                events.Add(VirtualKey(0x0D, true));
                continue;
            }
            if (character == '\n')
            {
                events.Add(VirtualKey(0x0D, false));
                events.Add(VirtualKey(0x0D, true));
                continue;
            }
            events.Add(Unicode(character, false));
            events.Add(Unicode(character, true));
        }
        return SendAll(events);
    }

    public static void SendCtrlV()
    {
        var events = new[]
        {
            VirtualKey(0x11, false),
            VirtualKey(0x56, false),
            VirtualKey(0x56, true),
            VirtualKey(0x11, true)
        };
        SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
    }

    public static bool ReleaseVirtualKey(ushort virtualKey)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var scan = (ushort)(MapVirtualKey(virtualKey, MapvkVkToVsc) & 0xFF);
        var flags = KeyeventfKeyup;
        if (virtualKey is 0xA3 or 0xA5 or 0x5B or 0x5C)
            flags |= KeyeventfExtendedKey;
        var input = new[]
        {
            new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Key = new KeyInput
                    {
                        VirtualKey = virtualKey,
                        Scan = scan,
                        Flags = flags
                    }
                }
            }
        };
        return SendInput(1, input, Marshal.SizeOf<Input>()) == 1;
    }

    public static bool TryGetText(out string text)
    {
        text = "";
        if (!TryOpen())
            return false;
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
                return true;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return false;
            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? "";
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TrySetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        if (!TryOpen())
            return false;
        try
        {
            EmptyClipboard();
            var memory = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
            if (memory == IntPtr.Zero)
                return false;
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(memory);
            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryEmpty()
    {
        if (!TryOpen())
            return false;
        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    static bool TryOpen()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(15);
        }
        return false;
    }

    static bool HasPasswordStyle(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;
        const int styleIndex = -16;
        const long password = 0x20;
        return (GetStyle(window, styleIndex) & password) != 0;
    }

    static long GetStyle(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index).ToInt64()
            : GetWindowLong32(window, index);
    }

    static void SetStyle(IntPtr window, int index, long value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(window, index, new IntPtr(value));
        else
            SetWindowLong32(window, index, (int)value);
    }

    static bool SendAll(List<Input> events)
    {
        if (events.Count == 0)
            return false;
        var sentAny = false;
        const int chunk = 64;
        for (var offset = 0; offset < events.Count; offset += chunk)
        {
            var count = Math.Min(chunk, events.Count - offset);
            var slice = events.GetRange(offset, count).ToArray();
            var sent = SendInput((uint)slice.Length, slice, Marshal.SizeOf<Input>());
            if (sent == 0 && !sentAny)
                return false;
            sentAny = true;
            if (sent != slice.Length)
                return true;
        }
        return true;
    }

    static Input Unicode(char character, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Key = new KeyInput
            {
                Scan = character,
                Flags = KeyeventfUnicode | (up ? KeyeventfKeyup : 0)
            }
        }
    };

    static Input VirtualKey(ushort key, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Key = new KeyInput
            {
                VirtualKey = key,
                Flags = up ? KeyeventfKeyup : 0
            }
        }
    };
}
