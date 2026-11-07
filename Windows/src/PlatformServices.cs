using System.Diagnostics;
using Microsoft.Win32;

namespace GigaTTS;

static class PlatformServices
{
    public static bool LaunchAtLogin() =>
        OperatingSystem.IsLinux() ? LinuxAutostart.IsEnabled() : WindowsLaunch.IsEnabled();

    public static void SetLaunchAtLogin(bool enabled)
    {
        if (OperatingSystem.IsLinux())
            LinuxAutostart.Set(enabled);
        else
            WindowsLaunch.Set(enabled);
    }

    public static void RequestMicrophone()
    {
        if (OperatingSystem.IsLinux())
            LinuxDesktop.OpenSound();
        else
            WindowsLaunch.Open("ms-settings:privacy-microphone");
    }

    public static void RequestSystemAudio()
    {
        if (OperatingSystem.IsLinux())
            LinuxDesktop.OpenSound();
        else
            WindowsLaunch.Open("ms-settings:sound");
    }

    public static void Reveal(string path)
    {
        if (OperatingSystem.IsLinux())
            LinuxDesktop.Reveal(path);
        else
            WindowsLaunch.Reveal(path);
    }

    public static bool WindowsMicrophoneAllowed()
    {
        try
        {
            using var global = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone");
            if (string.Equals(global?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase))
                return false;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return true;
            var leaf = exe.Replace('\\', '#').ToLowerInvariant();
            using var perApp = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\" + leaf);
            return !string.Equals(perApp?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}

static class PlatformClipboard
{
    public static bool TrySetText(string text) =>
        OperatingSystem.IsLinux() ? SetLinux(text) : SetWindows(text);

    static bool SetLinux(string text) => LinuxText.TrySetClipboard(text);

    static bool SetWindows(string text) => Native.TrySetText(text);
}

static class WindowsLaunch
{
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return !string.IsNullOrWhiteSpace(key?.GetValue("GigaTTS") as string);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("Не удалось открыть автозагрузку.");
        if (enabled)
            key.SetValue("GigaTTS", "\"" + (Environment.ProcessPath ?? "") + "\"");
        else
            key.DeleteValue("GigaTTS", false);
    }

    public static void Open(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
        {
            UseShellExecute = true
        });
    }
}

static class LinuxAutostart
{
    static string FilePath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(config)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : config;
            return Path.Combine(root, "autostart", "GigaTTS.desktop");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(FilePath))
                return false;
            var text = File.ReadAllText(FilePath);
            if (Flag(text, "Hidden", "true") || Flag(text, "X-GNOME-Autostart-enabled", "false"))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            return;
        }
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var exe = Environment.ProcessPath ?? "";
        var body =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Version=1.0\n" +
            "Name=GigaTTS\n" +
            "Comment=Локальная диктовка и расшифровка звонков\n" +
            "Exec=" + Quote(exe) + "\n" +
            "Terminal=false\n" +
            "Categories=Utility;\n" +
            "X-GNOME-Autostart-enabled=true\n";
        File.WriteAllText(FilePath, body);
    }

    static bool Flag(string text, string key, string value)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var split = line.IndexOf('=');
            if (split < 0)
                continue;
            return string.Equals(line[(split + 1)..].Trim(), value, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    static string Quote(string path)
    {
        if (path.IndexOfAny([' ', '\t', '"', '\\']) < 0)
            return path;
        return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

static class LinuxDesktop
{
    public static void OpenSound()
    {
        if (LinuxProc.Has("gnome-control-center"))
        {
            LinuxProc.StartDetached("gnome-control-center", "sound");
            return;
        }
        if (LinuxProc.Has("pavucontrol"))
        {
            LinuxProc.StartDetached("pavucontrol");
            return;
        }
        AppLog.Shared.Write("WARN", "Откройте параметры звука и проверьте микрофон и выход. Системный звук — это монитор колонок, не запись экрана.");
    }

    public static void Reveal(string path)
    {
        if (!LinuxProc.Has("xdg-open"))
        {
            AppLog.Shared.Write("INFO", "Файл: " + path);
            return;
        }
        var directory = Path.GetDirectoryName(path);
        LinuxProc.StartDetached("xdg-open", string.IsNullOrEmpty(directory) ? path : directory);
    }
}
