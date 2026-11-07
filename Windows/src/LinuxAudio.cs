using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GigaTTS;

static class LinuxPulse
{
    public static List<AudioDevice> Inputs()
    {
        var result = new List<AudioDevice> { new("", "Системный") };
        var text = LinuxProc.Capture("pactl", "list", "sources");
        if (!string.IsNullOrEmpty(text))
        {
            string? name = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Name:", StringComparison.Ordinal))
                    name = line["Name:".Length..].Trim();
                else if (line.StartsWith("Description:", StringComparison.Ordinal) && name != null)
                {
                    var description = line["Description:".Length..].Trim();
                    if (!name.EndsWith(".monitor", StringComparison.Ordinal))
                        result.Add(new AudioDevice(name, description.Length == 0 ? name : description));
                    name = null;
                }
            }
            return result;
        }
        var node = NodeName("@DEFAULT_AUDIO_SOURCE@");
        if (!string.IsNullOrEmpty(node))
            result.Add(new AudioDevice(node, node));
        return result;
    }

    public static bool HasMicrophone() => !string.IsNullOrEmpty(DefaultSource());

    public static bool HasMonitor() => !string.IsNullOrEmpty(DefaultMonitor());

    public static string? DefaultSource()
    {
        var pactl = LinuxProc.Capture("pactl", "get-default-source");
        if (!string.IsNullOrWhiteSpace(pactl))
            return pactl.Trim();
        return NodeName("@DEFAULT_AUDIO_SOURCE@");
    }

    public static string? DefaultMonitor()
    {
        var sink = LinuxProc.Capture("pactl", "get-default-sink");
        if (!string.IsNullOrWhiteSpace(sink))
            return sink.Trim() + ".monitor";
        var node = NodeName("@DEFAULT_AUDIO_SINK@");
        return string.IsNullOrEmpty(node) ? null : node + ".monitor";
    }

    static string? NodeName(string target)
    {
        var text = LinuxProc.Capture("wpctl", "inspect", target);
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("node.name", StringComparison.Ordinal))
                continue;
            var open = line.IndexOf('"');
            var close = line.LastIndexOf('"');
            if (open >= 0 && close > open)
                return line[(open + 1)..close];
        }
        return null;
    }
}

sealed class LinuxCaptureSession : ICaptureSession
{
    readonly StringBuilder _error = new();
    Process? _process;
    Stopwatch? _clock;
    string? _path;

    public event Action<string>? Failed;
    public bool IsRunning => _process is { HasExited: false };

    public double Duration
    {
        get
        {
            var fromFile = FileSeconds(_path);
            if (fromFile > 0)
                return fromFile;
            return _clock?.Elapsed.TotalSeconds ?? 0;
        }
    }

    public void StartMicrophone(string path, string deviceId)
    {
        var source = string.IsNullOrEmpty(deviceId) ? LinuxPulse.DefaultSource() : deviceId;
        if (string.IsNullOrEmpty(source))
            throw new InvalidOperationException("Микрофон не найден. Нужен PulseAudio или PipeWire и пакет pulseaudio-utils.");
        Start(path, source);
    }

    public void StartLoopback(string path)
    {
        var monitor = LinuxPulse.DefaultMonitor();
        if (string.IsNullOrEmpty(monitor))
            throw new InvalidOperationException("Не найден монитор выхода. Системный звук — это то, что воспроизводит компьютер (PulseAudio или PipeWire).");
        Start(path, monitor);
    }

    public string? Stop()
    {
        var process = _process;
        _process = null;
        var path = _path;
        if (process != null)
            LinuxSignals.Interrupt(process);
        _clock = null;
        _path = null;
        return path;
    }

    void Start(string path, string source)
    {
        Stop();
        var ffmpeg = LinuxProc.Which("ffmpeg")
            ?? throw new InvalidOperationException("Не найден ffmpeg. Нажмите «Установить распознавание» или поставьте пакет ffmpeg.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Delete(path);
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "pulse", "-i", source,
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", path
        })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        _error.Clear();
        Drain(process.StandardOutput, null);
        Drain(process.StandardError, _error);
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, process))
                return;
            var code = SafeCode(process);
            if (code is 0 or 255 or 130)
                return;
            var message = _error.ToString().Trim();
            Failed?.Invoke(string.IsNullOrEmpty(message) ? $"Запись остановилась (код {code})." : message);
        };
        if (process.WaitForExit(700))
        {
            Thread.Sleep(40);
            var message = _error.ToString().Trim();
            try { process.Dispose(); } catch { /* Already gone. */ }
            TryDelete(path);
            throw new InvalidOperationException(string.IsNullOrEmpty(message)
                ? "Не удалось начать запись."
                : "Не удалось начать запись. " + message);
        }
        _process = process;
        _path = path;
        _clock = Stopwatch.StartNew();
    }

    static void Drain(StreamReader reader, StringBuilder? sink)
    {
        var thread = new Thread(() =>
        {
            var buffer = new char[1024];
            try
            {
                while (true)
                {
                    var read = reader.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;
                    if (sink == null)
                        continue;
                    lock (sink)
                    {
                        sink.Append(buffer, 0, read);
                        if (sink.Length > 2000)
                            sink.Remove(0, sink.Length - 2000);
                    }
                }
            }
            catch
            {
                // The process was stopped.
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    static double FileSeconds(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return 0;
        try
        {
            var length = new FileInfo(path).Length;
            if (length <= 44)
                return 0;
            return (length - 44) / 32000.0;
        }
        catch
        {
            return 0;
        }
    }

    static int SafeCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* The next take replaces it. */ }
    }
}

sealed class LinuxPlayback : IPlayback
{
    readonly List<Process> _processes = new();
    bool _stopping;

    public bool IsPlaying { get; private set; }
    public event Action? Ended;

    public void Start(IReadOnlyList<string> paths)
    {
        Stop();
        foreach (var path in paths)
        {
            var process = StartOne(path);
            if (process == null)
                continue;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (_stopping)
                    return;
                if (_processes.All(item => item.HasExited))
                    Ended?.Invoke();
            };
            _processes.Add(process);
        }
        IsPlaying = _processes.Count > 0;
        if (!IsPlaying && paths.Count > 0)
            AppLog.Shared.Write("WARN", "Нечем воспроизвести запись. Нужен ffplay из пакета ffmpeg или paplay.");
    }

    public void Stop()
    {
        if (_processes.Count == 0)
        {
            IsPlaying = false;
            return;
        }
        _stopping = true;
        foreach (var process in _processes.ToArray())
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
                // Already stopped.
            }
            try { process.Dispose(); } catch { /* Already disposed. */ }
        }
        _processes.Clear();
        _stopping = false;
        IsPlaying = false;
    }

    static Process? StartOne(string path)
    {
        if (LinuxProc.Which("ffplay") is { } ffplay)
            return Launch(ffplay, "-nodisp", "-autoexit", "-loglevel", "error", path);
        if (LinuxProc.Which("paplay") is { } paplay)
            return Launch(paplay, path);
        if (LinuxProc.Which("pw-play") is { } pw)
            return Launch(pw, path);
        return null;
    }

    static Process? Launch(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            var process = Process.Start(info);
            if (process == null)
                return null;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process;
        }
        catch
        {
            return null;
        }
    }
}

static class LinuxSignals
{
    const int SigInt = 2;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int NativeKill(int pid, int sig);

    public static void Interrupt(Process process)
    {
        try
        {
            if (process.HasExited)
                return;
            if (OperatingSystem.IsLinux())
                NativeKill(process.Id, SigInt);
            if (!process.WaitForExit(3000) && !process.HasExited)
                process.Kill(true);
        }
        catch
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
        try { process.Dispose(); } catch { /* Already disposed. */ }
    }
}
