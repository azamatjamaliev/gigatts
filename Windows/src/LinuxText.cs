namespace GigaTTS;

static class LinuxText
{
    public static bool TrySetClipboard(string text)
    {
        if (LinuxSession.IsWayland && LinuxProc.Has("wl-copy") && LinuxProc.WriteStdin("wl-copy", text, "--type", "text/plain"))
            return true;
        if (LinuxProc.Has("xclip") && LinuxProc.WriteStdin("xclip", text, "-selection", "clipboard"))
            return true;
        if (LinuxProc.Has("xsel") && LinuxProc.WriteStdin("xsel", text, "--clipboard", "--input"))
            return true;
        if (LinuxProc.Has("wl-copy"))
            return LinuxProc.WriteStdin("wl-copy", text, "--type", "text/plain");
        return false;
    }

    public static InsertOutcome Insert(string text, out string? problem)
    {
        problem = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return InsertOutcome.Failed;
        if (LooksLikePasswordWindow())
        {
            AppLog.Shared.Write("WARN", "Вставка пропущена: заголовок окна похож на запрос пароля.");
            return InsertOutcome.Secure;
        }
        if (TryType(trimmed))
        {
            AppLog.Shared.Write("INFO", "Вставка через ввод символов.");
            return InsertOutcome.Inserted;
        }
        if (TryPaste(trimmed))
        {
            AppLog.Shared.Write("INFO", "Вставка через Ctrl+V.");
            return InsertOutcome.Inserted;
        }
        problem = "Не удалось вставить текст. На X11 нужны xdotool и xclip, на Wayland — wtype и wl-clipboard. Текст сохранён в меню.";
        AppLog.Shared.Write("WARN", problem);
        return InsertOutcome.Failed;
    }

    public static Avalonia.PixelPoint Cursor()
    {
        var text = LinuxProc.Capture("xdotool", "getmouselocation", "--shell");
        if (string.IsNullOrEmpty(text))
            return new Avalonia.PixelPoint(0, 0);
        var x = 0;
        var y = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("X=", StringComparison.Ordinal))
                int.TryParse(line.AsSpan(2), out x);
            else if (line.StartsWith("Y=", StringComparison.Ordinal))
                int.TryParse(line.AsSpan(2), out y);
        }
        return new Avalonia.PixelPoint(x, y);
    }

    static bool TryType(string text)
    {
        if (LinuxSession.IsWayland && LinuxProc.Has("wtype") && !text.StartsWith('-'))
            return LinuxProc.Run("wtype", 8000, text) == 0;
        if (LinuxProc.Has("ydotool") && LinuxProc.Run("ydotool", 8000, "type", "--", text) == 0)
            return true;
        if (LinuxSession.IsX11 && IsAscii(text) && LinuxProc.Has("xdotool"))
            return LinuxProc.Run("xdotool", 8000, "type", "--clearmodifiers", "--", text) == 0;
        return false;
    }

    static bool TryPaste(string text)
    {
        var previous = ReadClipboard();
        if (!TrySetClipboard(text))
            return false;
        Thread.Sleep(80);
        if (!SendCtrlV())
            return false;
        if (previous == null)
            return true;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TrySetClipboard(previous);
        };
        timer.Start();
        return true;
    }

    static bool SendCtrlV()
    {
        if (LinuxSession.IsWayland && LinuxProc.Has("wtype"))
            return LinuxProc.Run("wtype", 4000, "-M", "ctrl", "-k", "v", "-m", "ctrl") == 0;
        if (LinuxProc.Has("xdotool") && LinuxProc.Run("xdotool", 4000, "key", "--clearmodifiers", "ctrl+v") == 0)
            return true;
        if (LinuxProc.Has("ydotool"))
            return LinuxProc.Run("ydotool", 4000, "key", "29:1", "47:1", "47:0", "29:0") == 0;
        if (LinuxProc.Has("wtype"))
            return LinuxProc.Run("wtype", 4000, "-M", "ctrl", "-k", "v", "-m", "ctrl") == 0;
        return false;
    }

    static string? ReadClipboard()
    {
        if (LinuxSession.IsWayland && LinuxProc.Has("wl-paste"))
            return LinuxProc.Capture("wl-paste", "--no-newline");
        if (LinuxProc.Has("xclip"))
            return LinuxProc.Capture("xclip", "-selection", "clipboard", "-o");
        if (LinuxProc.Has("xsel"))
            return LinuxProc.Capture("xsel", "--clipboard", "--output");
        if (LinuxProc.Has("wl-paste"))
            return LinuxProc.Capture("wl-paste", "--no-newline");
        return null;
    }

    static bool LooksLikePasswordWindow()
    {
        if (!LinuxProc.Has("xdotool"))
            return false;
        var name = LinuxProc.Capture("xdotool", "getactivewindow", "getwindowname") ?? "";
        var cls = LinuxProc.Capture("xdotool", "getactivewindow", "getwindowclassname") ?? "";
        var blob = (name + " " + cls).ToLowerInvariant();
        if (blob.Length == 0)
            return false;
        return blob.Contains("password", StringComparison.Ordinal)
            || blob.Contains("passwd", StringComparison.Ordinal)
            || blob.Contains("парол", StringComparison.Ordinal)
            || blob.Contains("keyring", StringComparison.Ordinal)
            || blob.Contains("polkit", StringComparison.Ordinal);
    }

    static bool IsAscii(string text)
    {
        foreach (var character in text)
        {
            if (character is '\n' or '\t')
                continue;
            if (character < 32 || character > 126)
                return false;
        }
        return true;
    }
}
