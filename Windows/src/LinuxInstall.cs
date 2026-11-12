using System.Diagnostics;
using System.Text;

namespace GigaTTS;

static class LinuxInstaller
{
    public static Task Install(Action<string> log, CancellationToken cancel) => new Runner(log, cancel).Run();

    sealed class Runner
    {
        readonly Action<string> _log;
        readonly CancellationToken _cancel;

        public Runner(Action<string> log, CancellationToken cancel)
        {
            _log = log;
            _cancel = cancel;
        }

        public async Task Run()
        {
            _log("Приложение: " + AppContext.BaseDirectory + "\n");
            _log("Данные: " + AppPaths.Support + "\n");
            var workerSource = WorkerLocator.BundledWorker()
                ?? throw new InvalidOperationException("В приложении нет worker.py. Пересоберите GigaПисец.");
            var requirementsSource = WorkerLocator.BundledRequirements()
                ?? throw new InvalidOperationException("В приложении нет requirements.txt. Пересоберите GigaПисец.");
            var installedWorker = Path.Combine(AppPaths.Support, "worker.py");
            var requirements = Path.Combine(AppPaths.Support, "requirements.txt");
            File.Copy(workerSource, installedWorker, true);
            File.Copy(requirementsSource, requirements, true);

            var python = await FindPython();
            var required = new List<string>();
            if (python == null)
            {
                required.Add("python3");
                required.Add("python3-venv");
                required.Add("python3-pip");
            }
            else if (!await HasModule(python, "import venv, ensurepip"))
            {
                required.Add("python3-venv");
            }
            if (!LinuxProc.Has("ffmpeg"))
                required.Add("ffmpeg");
            if (!LinuxProc.Has("git"))
                required.Add("git");
            if (!LinuxProc.Has("pactl") && !LinuxProc.Has("wpctl"))
                required.Add("pulseaudio-utils");
            if (!LinuxProc.Has("xdotool"))
                required.Add("xdotool");
            if (!LinuxProc.Has("xclip"))
                required.Add("xclip");
            if (!LinuxProc.Has("wl-copy"))
                required.Add("wl-clipboard");
            if (required.Count > 0)
                await Apt(required, required: true);
            else
                _log("Системные пакеты уже на месте.\n");

            var optional = new List<string>();
            if (!LinuxProc.Has("wtype"))
                optional.Add("wtype");
            if (!IndicatorInstalled())
                optional.Add("libayatana-appindicator3-1");
            if (optional.Count > 0)
                await Apt(optional, required: false);

            python = await FindPython()
                ?? throw new InvalidOperationException("Не найден Python 3.10 или новее. " + Manual(new[] { "python3", "python3-venv", "python3-pip" }));
            _log("Python: " + python + "\n");
            if (LinuxProc.Which("ffmpeg") is { } ffmpeg)
                _log("ffmpeg: " + ffmpeg + "\n");
            if (LinuxProc.Which("git") is { } git)
                _log("Git: " + git + "\n");

            var venv = Path.Combine(AppPaths.Support, "venv");
            var venvPython = Path.Combine(venv, "bin", "python");
            if (!File.Exists(venvPython))
            {
                _log("Создаю локальное окружение…\n");
                await Run(python, "-m", "venv", venv);
            }
            if (!File.Exists(venvPython))
                throw new InvalidOperationException("Не удалось создать окружение Python.");

            _log("Устанавливаю PyTorch, GigaAM и pyannote. Первый раз это занимает несколько минут…\n");
            _log("Видеодрайвер NVIDIA этот шаг не ставит. Если в PyTorch нет CUDA, распознавание перейдёт на процессор.\n");
            await Run(venvPython, "-u", "-m", "pip", "install", "--upgrade", "pip");
            await Run(venvPython, "-u", "-m", "pip", "install", "-r", requirements);
            if (WorkerLocator.BundledDiarizationModel() is { } bundled)
            {
                _log("Использую встроенную модель разделения спикеров: " + bundled + "\n");
            }
            else
            {
                _log("Скачиваю модель разделения спикеров (pyannote)…\n");
                var diarization = await RunAllowFail(venvPython, "-u", installedWorker, "--download-diarization");
                if (diarization != 0)
                {
                    _log("Разделение спикеров отложено: в сборке нет models/, либо задайте HF_TOKEN "
                        + "(или ~/.cache/gigatts/hf-token). Диктовка и две дорожки звонка работают без этого.\n");
                }
            }

            var extras = new List<string>();
            var ffmpegDir = Path.GetDirectoryName(LinuxProc.Which("ffmpeg"));
            if (!string.IsNullOrEmpty(ffmpegDir))
                extras.Add(ffmpegDir);
            File.WriteAllText(Path.Combine(AppPaths.Support, "path-extra.txt"), string.Join('\n', extras) + "\n");
            File.WriteAllText(Path.Combine(AppPaths.Support, "python-path"), venvPython + "\n");
            File.WriteAllText(Path.Combine(AppPaths.Support, "worker-path"), installedWorker + "\n");
            _log("Файлы распознавания готовы.\n");
        }

        async Task Apt(List<string> packages, bool required)
        {
            if (packages.Count == 0)
                return;
            var distinct = packages.Distinct(StringComparer.Ordinal).ToList();
            var manual = Manual(distinct);
            if (!LinuxProc.Has("pkexec"))
            {
                if (!required)
                {
                    _log("Необязательные пакеты не поставлены, нет pkexec: " + string.Join(' ', distinct) + "\n");
                    return;
                }
                throw new InvalidOperationException("Нет pkexec, чтобы поставить пакеты. Выполните в терминале:\n" + manual);
            }
            _log("Сейчас может появиться запрос пароля администратора — так apt ставит пакеты.\n");
            var script = "export DEBIAN_FRONTEND=noninteractive; apt-get update || true; apt-get install -y " + string.Join(' ', distinct);
            var code = await RunAllowFail("pkexec", "/bin/sh", "-c", script);
            if (code == 0)
                return;
            if (!required)
            {
                _log($"Предупреждение: необязательные пакеты не установлены, код {code}. Команда:\n{manual}\n");
                return;
            }
            throw new InvalidOperationException($"Не удалось установить пакеты (код {code}). Выполните в терминале:\n{manual}");
        }

        async Task<string?> FindPython()
        {
            foreach (var name in new[] { "python3.13", "python3.12", "python3.11", "python3.10", "python3" })
            {
                var candidate = LinuxProc.Which(name);
                if (candidate == null || IsInsideVenv(candidate))
                    continue;
                if (await IsPython310(candidate))
                    return candidate;
            }
            return null;
        }

        async Task<bool> IsPython310(string path)
        {
            var version = await Capture(path, "-c", "import sys; print(f'{sys.version_info[0]}.{sys.version_info[1]}')");
            if (version == null)
                return false;
            var parts = version.Trim().Split('.');
            return parts.Length >= 2
                && int.TryParse(parts[0], out var major)
                && int.TryParse(parts[1], out var minor)
                && (major > 3 || (major == 3 && minor >= 10));
        }

        async Task<bool> HasModule(string python, string statement)
        {
            var code = await RunAllowFail(python, "-c", statement);
            return code == 0;
        }

        static bool IsInsideVenv(string path)
        {
            var root = Path.GetFullPath(Path.Combine(AppPaths.Support, "venv")) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
        }

        async Task Run(string file, params string[] args)
        {
            var code = await RunAllowFail(file, args);
            if (code != 0)
                throw new InvalidOperationException($"{Path.GetFileName(file)} завершился с кодом {code}.");
        }

        async Task<int> RunAllowFail(string file, params string[] args)
        {
            _cancel.ThrowIfCancellationRequested();
            var path = LinuxProc.Which(file) ?? file;
            _log("\n$ " + Path.GetFileName(path) + " " + string.Join(' ', args.Select(Quote)) + "\n");
            var info = new ProcessStartInfo(path)
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
            info.Environment["PYTHONUNBUFFERED"] = "1";
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            info.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            WorkerLocator.ApplyDiarizationEnvironment(info);
            using var process = new Process { StartInfo = info };
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить " + path);
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
                await stderr;
                if (process.ExitCode != 0)
                    return null;
                return (await stdout).Trim();
            }
            catch
            {
                return null;
            }
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

        static bool IndicatorInstalled()
        {
            foreach (var path in new[]
            {
                "/usr/lib/x86_64-linux-gnu/libayatana-appindicator3.so.1",
                "/usr/lib/aarch64-linux-gnu/libayatana-appindicator3.so.1",
                "/usr/lib/libayatana-appindicator3.so.1"
            })
            {
                if (File.Exists(path))
                    return true;
            }
            return false;
        }

        static string Manual(IEnumerable<string> packages) =>
            "sudo apt-get update && sudo apt-get install -y " + string.Join(' ', packages);

        static string Quote(string value)
        {
            if (value.Length == 0)
                return "\"\"";
            return value.IndexOfAny([' ', '\t', '"']) < 0 ? value : "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
