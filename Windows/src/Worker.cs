using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GigaTTS;

static class WorkerLocator
{
    public readonly record struct Located(string Python, string Script);

    public static Located? Locate()
    {
        var envPython = Environment.GetEnvironmentVariable("GIGATTS_PYTHON");
        var envScript = Environment.GetEnvironmentVariable("GIGATTS_WORKER");
        if (File.Exists(envPython) && File.Exists(envScript))
            return new Located(envPython!, envScript!);

        var pythonFile = Path.Combine(AppPaths.Support, "python-path");
        var scriptFile = Path.Combine(AppPaths.Support, "worker-path");
        if (File.Exists(pythonFile) && File.Exists(scriptFile))
        {
            var python = File.ReadAllText(pythonFile).Trim();
            var script = File.ReadAllText(scriptFile).Trim();
            if (File.Exists(python) && File.Exists(script))
                return new Located(python, script);
        }

        if (VenvPython(Path.Combine(AppPaths.Support, "venv")) is { } venvPython)
        {
            var supportWorker = Path.Combine(AppPaths.Support, "worker.py");
            if (File.Exists(supportWorker))
                return new Located(venvPython, supportWorker);
            if (BundledWorker() is { } bundled)
                return new Located(venvPython, bundled);
        }

        foreach (var root in SearchRoots())
        {
            foreach (var directory in new[] { Path.Combine(root, "Worker"), root })
            {
                var script = Path.Combine(directory, "worker.py");
                var python = VenvPython(Path.Combine(directory, ".venv"));
                if (python != null && File.Exists(script))
                    return new Located(python, script);
            }
        }
        return null;
    }

    public static string? BundledWorker() => Bundled("worker.py");
    public static string? BundledRequirements() => Bundled("requirements.txt");

    public static string ExtraPath()
    {
        var file = Path.Combine(AppPaths.Support, "path-extra.txt");
        if (!File.Exists(file))
            return "";
        var parts = File.ReadAllLines(file).Select(line => line.Trim()).Where(Directory.Exists);
        return string.Join(Path.PathSeparator, parts);
    }

    static string? VenvPython(string root)
    {
        foreach (var relative in new[]
        {
            Path.Combine("Scripts", "python.exe"),
            Path.Combine("bin", "python"),
            Path.Combine("bin", "python3")
        })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    static string? Bundled(string name)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside))
            return beside;
        foreach (var root in SearchRoots())
        {
            var candidate = Path.Combine(root, "Worker", name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var cursor = start;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(cursor); depth++)
            {
                if (seen.Add(cursor))
                    yield return cursor;
                cursor = Path.GetDirectoryName(cursor) ?? "";
            }
        }
    }
}

sealed class WorkerInstaller
{
    readonly Action<string> _log;
    readonly CancellationToken _cancel;
    string _prefix = "";

    WorkerInstaller(Action<string> log, CancellationToken cancel)
    {
        _log = log;
        _cancel = cancel;
    }

    public static Task Install(Action<string> log, CancellationToken cancel)
    {
        if (OperatingSystem.IsLinux())
            return LinuxInstaller.Install(log, cancel);
        return new WorkerInstaller(log, cancel).Run();
    }

    async Task Run()
    {
        _log("Приложение: " + AppContext.BaseDirectory + "\n");
        _log("Данные: " + AppPaths.Support + "\n");
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        AddDirectory(windowsApps);

        var workerSource = WorkerLocator.BundledWorker()
            ?? throw new InvalidOperationException("В приложении нет worker.py. Пересоберите GigaTTS.");
        var requirementsSource = WorkerLocator.BundledRequirements()
            ?? throw new InvalidOperationException("В приложении нет requirements.txt. Пересоберите GigaTTS.");
        var installedWorker = Path.Combine(AppPaths.Support, "worker.py");
        var requirements = Path.Combine(AppPaths.Support, "requirements.txt");
        File.Copy(workerSource, installedWorker, true);
        File.Copy(requirementsSource, requirements, true);

        var git = await Ensure("Git", "Git.Git", () => Task.FromResult(LocateGit()));
        AddFile(git);
        var python = await Ensure("Python 3.10+", "Python.Python.3.12", FindPython);
        var ffmpeg = await Ensure("ffmpeg", "Gyan.FFmpeg", () => Task.FromResult(FindFfmpeg()));
        AddFile(ffmpeg);

        _log("Проверяю Visual C++ Redistributable…\n");
        var redist = await RunWinget("install", "--id", "Microsoft.VCRedist.2015+.x64", "-e", "--scope", "user",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity", "--silent");
        if (redist != 0)
            _log($"Предупреждение: Visual C++ Redistributable не подтверждён, код winget {redist}.\n");

        var venv = Path.Combine(AppPaths.Support, "venv");
        var venvPython = Path.Combine(venv, "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            _log("Создаю локальное окружение…\n");
            await Run(python, "-m", "venv", venv);
        }
        if (!File.Exists(venvPython))
            throw new InvalidOperationException("Не удалось создать окружение Python.");

        _log("Устанавливаю PyTorch и GigaAM. Первый раз это занимает несколько минут…\n");
        await Run(venvPython, "-u", "-m", "pip", "install", "--upgrade", "pip");
        await Run(venvPython, "-u", "-m", "pip", "install", "-r", requirements);

        var extras = new List<string>();
        var ffmpegDir = Path.GetDirectoryName(ffmpeg);
        if (!string.IsNullOrEmpty(ffmpegDir))
            extras.Add(ffmpegDir);
        File.WriteAllText(Path.Combine(AppPaths.Support, "path-extra.txt"), string.Join('\n', extras) + "\n");
        File.WriteAllText(Path.Combine(AppPaths.Support, "python-path"), venvPython + "\n");
        File.WriteAllText(Path.Combine(AppPaths.Support, "worker-path"), installedWorker + "\n");
        _log("Файлы распознавания готовы.\n");
    }

    async Task<string> Ensure(string title, string packageId, Func<Task<string?>> locate)
    {
        var found = await locate();
        if (found != null)
        {
            _log($"{title}: {found}\n");
            return found;
        }
        if (Which("winget.exe") == null && Which("winget") == null)
            throw new InvalidOperationException($"Нужен winget, чтобы поставить {title}: https://aka.ms/getwinget");
        _log($"{title} не найден, ставлю через winget…\n");
        var code = await RunWinget("install", "--id", packageId, "-e", "--scope", "user",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity", "--silent");
        found = await locate();
        if (found == null)
            throw new InvalidOperationException($"Не удалось установить {title}. Код winget: {code}.");
        _log($"{title}: {found}\n");
        return found;
    }

    async Task<string?> FindPython()
    {
        var py = Which("py.exe") ?? Which("py");
        if (py != null)
        {
            foreach (var version in new[] { "3.13", "3.12", "3.11", "3.10" })
            {
                var printed = await Capture(py, "-" + version, "-c", "import sys; print(sys.executable)");
                if (await IsPython310(printed))
                    return printed!.Trim();
            }
        }
        foreach (var name in new[] { "python.exe", "python3.exe", "python", "python3" })
        {
            var candidate = Which(name);
            if (await IsPython310(candidate))
                return candidate;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var version in new[] { "313", "312", "311", "310" })
        {
            foreach (var candidate in new[]
            {
                Path.Combine(local, "Programs", "Python", "Python" + version, "python.exe"),
                Path.Combine(programFiles, "Python" + version, "python.exe")
            })
            {
                if (await IsPython310(candidate))
                    return candidate;
            }
        }
        return null;
    }

    string? LocateGit()
    {
        var found = Which("git.exe") ?? Which("git");
        if (found != null)
            return found;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
        {
            Path.Combine(programFiles, "Git", "cmd", "git.exe"),
            Path.Combine(local, "Programs", "Git", "cmd", "git.exe")
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    static string? FindFfmpeg()
    {
        var found = WhichStatic("ffmpeg.exe") ?? WhichStatic("ffmpeg");
        if (found != null)
            return found;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(link))
            return link;
        return FindUnder(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "ffmpeg.exe", 6)
            ?? FindUnder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg"), "ffmpeg.exe", 4);
    }

    async Task<bool> IsPython310(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path.Trim()))
            return false;
        var version = await Capture(path.Trim(), "-c", "import sys; print(f'{sys.version_info[0]}.{sys.version_info[1]}')");
        if (version == null)
            return false;
        var parts = version.Trim().Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && (major > 3 || (major == 3 && minor >= 10));
    }

    async Task Run(string file, params string[] args)
    {
        var code = await Start(file, args);
        if (code != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} завершился с кодом {code}.");
    }

    async Task<int> RunWinget(params string[] args)
    {
        var command = "chcp 65001>nul & winget " + string.Join(' ', args.Select(Quote));
        _log("\n$ winget " + string.Join(' ', args.Select(Quote)) + "\n");
        var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return await Start(comspec, new[] { "/d", "/s", "/c", command }, alreadyLogged: true);
    }

    async Task<int> Start(string file, IReadOnlyList<string> args, bool alreadyLogged = false)
    {
        _cancel.ThrowIfCancellationRequested();
        if (!alreadyLogged)
            _log("\n$ " + Path.GetFileName(file) + " " + string.Join(' ', args.Select(Quote)) + "\n");
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Support
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!string.IsNullOrEmpty(_prefix))
            path = _prefix + Path.PathSeparator + path;
        info.Environment["PATH"] = path;
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить " + file);
        using var registration = _cancel.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* Already gone. */ }
        });
        var output = Pump(process.StandardOutput);
        var error = Pump(process.StandardError);
        try
        {
            await process.WaitForExitAsync(_cancel);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* Already gone. */ }
            throw;
        }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    async Task Pump(StreamReader reader)
    {
        var buffer = new char[2048];
        var pending = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), _cancel);
                if (read == 0)
                    break;
                pending.Append(buffer, 0, read);
                var text = pending.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
                var last = text.LastIndexOf('\n');
                if (last < 0)
                    continue;
                _log(text[..(last + 1)]);
                pending.Clear();
                pending.Append(text[(last + 1)..]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }
        if (pending.Length > 0)
            _log(pending.ToString());
    }

    async Task<string?> Capture(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
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
            using var process = Process.Start(info);
            if (process == null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                return null;
            return (await stdout).Trim();
        }
        catch
        {
            return null;
        }
    }

    string? Which(string name) => WhichIn(name, CurrentPath());

    static string? WhichStatic(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return WhichIn(name, path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
    }

    string CurrentPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (string.IsNullOrEmpty(_prefix))
            return path;
        return _prefix + Path.PathSeparator + path;
    }

    static string? WhichIn(string name, string path) =>
        WhichIn(name, path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

    static string? WhichIn(string name, IEnumerable<string> directories)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var directory in directories)
        {
            var direct = Path.Combine(directory, name);
            if (File.Exists(direct))
                return direct;
            foreach (var extension in extensions)
            {
                var candidate = direct.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? direct : direct + extension;
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    static string? FindUnder(string root, string fileName, int maxDepth)
    {
        if (!Directory.Exists(root))
            return null;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
                if (depth >= maxDepth)
                    continue;
                foreach (var child in Directory.EnumerateDirectories(dir))
                    queue.Enqueue((child, depth + 1));
            }
            catch
            {
                // Skip folders we cannot read.
            }
        }
        return null;
    }

    void AddFile(string? file)
    {
        if (!string.IsNullOrWhiteSpace(file))
            AddDirectory(Path.GetDirectoryName(file));
    }

    void AddDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        var parts = _prefix.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
            return;
        _prefix = string.IsNullOrEmpty(_prefix) ? directory : directory + Path.PathSeparator + _prefix;
    }

    static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        return value.IndexOfAny([' ', '\t', '"']) < 0 ? value : "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

static class WorkerRuntime
{
    public static readonly string[] TruststoreInstallArguments =
    [
        "-m", "pip", "install", "--disable-pip-version-check", "truststore"
    ];

    static bool _truststoreReady;

    public static void SyncBundledScript()
    {
        var source = WorkerLocator.BundledWorker();
        if (string.IsNullOrEmpty(source))
            return;
        var dest = Path.Combine(AppPaths.Support, "worker.py");
        try
        {
            var fullSource = Path.GetFullPath(source);
            var fullDest = Path.GetFullPath(dest);
            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(AppPaths.Support);
            File.Copy(fullSource, fullDest, true);
        }
        catch
        {
            // The previously installed script remains.
        }
    }

    public static string ScriptToLaunch(string located)
    {
        var refreshed = Path.Combine(AppPaths.Support, "worker.py");
        if (!File.Exists(refreshed) || string.IsNullOrEmpty(located))
            return located;
        try
        {
            var root = Path.GetFullPath(AppPaths.Support);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;
            if (Path.GetFullPath(located).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return refreshed;
        }
        catch
        {
            // Keep the path the locator already resolved.
        }
        return located;
    }

    public static async Task<bool> EnsureTruststore(string python)
    {
        if (_truststoreReady || string.IsNullOrEmpty(python) || !File.Exists(python))
            return false;
        if (await RunQuiet(python, 15, "-c", "import truststore"))
        {
            _truststoreReady = true;
            return false;
        }
        var installed = await RunQuiet(python, 30, TruststoreInstallArguments);
        if (installed)
            _truststoreReady = true;
        return installed;
    }

    static async Task<bool> RunQuiet(string python, int seconds, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppPaths.Support
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            info.Environment["PYTHONUNBUFFERED"] = "1";
            info.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            using var process = new Process { StartInfo = info };
            if (!process.Start())
                return false;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { /* Already gone. */ }
                return false;
            }
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

sealed class WorkerClient
{
    readonly object _gate = new();
    readonly object _write = new();
    readonly Dictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
    Process? _process;
    StreamWriter? _stdin;

    public event Action<string>? Log;
    public event Action<int>? Exited;

    public void Start(string python, string script, string device)
    {
        Stop();
        if (!File.Exists(python))
            throw new InvalidOperationException("Не найден Python: " + python);
        if (!File.Exists(script))
            throw new InvalidOperationException("Не найден worker.py: " + script);
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script) ?? AppPaths.Support
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(script);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extra = WorkerLocator.ExtraPath();
        if (!string.IsNullOrEmpty(extra))
            path = extra + Path.PathSeparator + path;
        info.Environment["PATH"] = path;
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["GIGATTS_DEVICE"] = device;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_process, process))
                return;
            var code = SafeCode(process);
            FailPending($"Процесс распознавания завершился (код {code}).");
            Exited?.Invoke(code);
        };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить распознавание.");
        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _ = ReadLines(process.StandardOutput);
        _ = ReadErrors(process.StandardError);
    }

    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        _stdin = null;
        if (process == null)
        {
            FailPending("Распознавание остановлено.");
            return;
        }
        try { process.StandardInput.Close(); } catch { /* Already closed. */ }
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch { /* Already exited. */ }
        try { process.Dispose(); } catch { /* Already disposed. */ }
        FailPending("Распознавание остановлено.");
    }

    public Task<JsonDocument> RequestAsync(JsonObject body, CancellationToken cancel = default)
    {
        var id = Guid.NewGuid().ToString("N");
        body["id"] = id;
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _pending[id] = completion;
        try
        {
            var line = body.ToJsonString() + "\n";
            lock (_write)
            {
                if (_stdin == null)
                    throw new InvalidOperationException("Воркер не запущен.");
                _stdin.Write(line);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _pending.Remove(id);
            completion.TrySetException(ex);
        }
        if (cancel.CanBeCanceled)
        {
            var registration = cancel.Register(() => completion.TrySetCanceled(cancel));
            _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return completion.Task;
    }

    async Task ReadLines(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                    Handle(line);
            }
        }
        catch
        {
            // The process was stopped.
        }
    }

    async Task ReadErrors(StreamReader reader)
    {
        var buffer = new char[2048];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory());
                if (read == 0)
                    break;
                Log?.Invoke(new string(buffer, 0, read));
            }
        }
        catch
        {
            // The process was stopped.
        }
    }

    void Handle(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch
        {
            return;
        }
        var id = document.RootElement.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? "" : "";
        TaskCompletionSource<JsonDocument>? completion;
        lock (_gate)
            _pending.Remove(id, out completion);
        if (completion == null)
        {
            document.Dispose();
            return;
        }
        var ok = document.RootElement.TryGetProperty("ok", out var okProperty) && okProperty.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var error = document.RootElement.TryGetProperty("error", out var errorProperty)
                ? errorProperty.GetString()
                : "Ошибка распознавания";
            document.Dispose();
            completion.TrySetException(new InvalidOperationException(error ?? "Ошибка распознавания"));
            return;
        }
        completion.TrySetResult(document);
    }

    void FailPending(string message)
    {
        List<TaskCompletionSource<JsonDocument>> waiters;
        lock (_gate)
        {
            waiters = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var waiter in waiters)
            waiter.TrySetException(new InvalidOperationException(message));
    }

    static int SafeCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }
}
