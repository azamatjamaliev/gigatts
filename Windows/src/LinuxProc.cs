using System.Diagnostics;
using System.Text;

namespace GigaTTS;

static class LinuxSession
{
    public static string Kind
    {
        get
        {
            var type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant() ?? "";
            if (type is "wayland" or "x11" or "tty" or "mir")
                return type;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                return "wayland";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                return "x11";
            return type.Length == 0 ? "unknown" : type;
        }
    }

    public static bool IsWayland => Kind == "wayland";
    public static bool IsX11 => Kind == "x11";
}

static class LinuxProc
{
    public static bool Has(string name) => Which(name) != null;

    public static string? Which(string name)
    {
        if (name.Contains('/'))
            return File.Exists(name) ? name : null;
        var dirs = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!dirs.Contains(dir))
                dirs.Add(dir);
        }
        foreach (var extra in new[] { "/usr/local/bin", "/usr/bin", "/bin" })
        {
            if (!dirs.Contains(extra))
                dirs.Add(extra);
        }
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string? Capture(string file, params string[] args)
    {
        if (!TryStart(file, args, out var process) || process == null)
            return null;
        try
        {
            var stdout = "";
            var stdoutThread = new Thread(() => stdout = process.StandardOutput.ReadToEnd()) { IsBackground = true };
            var stderrThread = new Thread(() => process.StandardError.ReadToEnd()) { IsBackground = true };
            stdoutThread.Start();
            stderrThread.Start();
            if (!process.WaitForExit(8000))
            {
                Kill(process);
                return null;
            }
            stdoutThread.Join(1000);
            stderrThread.Join(1000);
            if (process.ExitCode != 0)
                return null;
            return stdout.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    public static int Run(string file, int timeoutMs, params string[] args)
    {
        if (!TryStart(file, args, out var process) || process == null)
            return -1;
        try
        {
            var stdoutThread = new Thread(() => process.StandardOutput.ReadToEnd()) { IsBackground = true };
            var stderrThread = new Thread(() => process.StandardError.ReadToEnd()) { IsBackground = true };
            stdoutThread.Start();
            stderrThread.Start();
            if (!process.WaitForExit(timeoutMs))
            {
                Kill(process);
                return -1;
            }
            stdoutThread.Join(1000);
            stderrThread.Join(1000);
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
        finally
        {
            try { process.Dispose(); } catch { /* Already gone. */ }
        }
    }

    public static bool WriteStdin(string file, string text, params string[] args)
    {
        var path = Which(file);
        if (path == null)
            return false;
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            var process = Process.Start(info);
            if (process == null)
                return false;
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(400))
            {
                var ok = process.ExitCode == 0;
                process.Dispose();
                return ok;
            }
            // xclip and wl-copy may stay alive to own the clipboard selection.
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartDetached(string file, params string[] args)
    {
        var path = Which(file);
        if (path == null)
            return;
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            Process.Start(info);
        }
        catch
        {
            // The settings app is optional.
        }
    }

    static bool TryStart(string file, string[] args, out Process? process)
    {
        process = null;
        var path = Which(file);
        if (path == null)
            return false;
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            process = Process.Start(info);
            return process != null;
        }
        catch
        {
            return false;
        }
    }

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // Already gone.
        }
    }
}
