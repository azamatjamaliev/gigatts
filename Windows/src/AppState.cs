using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace GigaTTS;

public sealed class AppState : Observable
{
    readonly WorkerClient _worker = new();
    readonly IHotkeySource _hotkeys = HotkeySources.Create();
    readonly OverlayController _overlay = new();
    readonly ICaptureSession _microphone = CaptureFactory.Create();
    readonly ICaptureSession _loopback = CaptureFactory.Create();
    readonly DispatcherTimer _callTimer;
    CallRecord? _activeCall;
    int _generation;
    int _armGeneration;
    bool _armed;
    bool _suppressRelease;
    bool _handsFree;
    bool _bootstrapped;
    bool _shutdown;
    bool _menuQueued;
    string _logTail = "";
    string _runningDevice = "";
    double _callElapsed;
    CancellationTokenSource? _installCancel;

    public AppState()
    {
        _hotkeys.App = this;
        _worker.Log += text => OnUi(() =>
        {
            Absorb(text);
            Note("Воркер: " + text, "WORKER");
        });
        _worker.Exited += code => OnUi(() =>
        {
            if (_shutdown || ModelKind == ModelKind.Loading)
                return;
            Note($"Процесс распознавания завершился. code={code}", "ERROR");
            if (ModelKind == ModelKind.Ready)
                SetModel(ModelKind.Failed, $"Процесс распознавания остановился (код {code}).");
        });
        _microphone.Failed += message => OnUi(() =>
        {
            LastError = message;
            Raise(nameof(LastError));
            Note(message, "ERROR");
            if (Phase == Phase.Dictating)
                CancelDictation();
            Flash(message);
        });
        _loopback.Failed += message => OnUi(() =>
        {
            LastError = "Системный звук: " + message;
            Raise(nameof(LastError));
        });
        _callTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _callTimer.Tick += (_, _) =>
        {
            if (Phase != Phase.RecordingCall || _activeCall == null)
                return;
            _callElapsed = (DateTime.Now - _activeCall.CreatedAt).TotalSeconds;
            _overlay.ShowCall(Format.Minutes(_callElapsed));
        };
    }

    public event EventHandler? MenuChanged;
    public event EventHandler? CallsReloaded;
    public Action? OnNeedOnboarding { get; set; }
    public Action? OnOpenCalls { get; set; }
    public Action? OnOpenSettings { get; set; }

    public ObservableCollection<DictationEntry> History { get; } = new();
    public ObservableCollection<CallRecord> Calls { get; } = new();
    public ObservableCollection<AudioDevice> Devices { get; } = new();

    public Phase Phase { get; private set; }
    public bool HandsFree => _handsFree;
    public ModelKind ModelKind { get; private set; } = ModelKind.Starting;
    public bool ModelReady => ModelKind == ModelKind.Ready;
    public string StatusDetail { get; private set; } = "";
    public Hotkey Hotkey { get; private set; } = Hotkey.RightAlt;
    public bool IsCapturingHotkey { get; private set; }
    public string SelectedDeviceId { get; private set; } = "";
    public string ModelDevice { get; private set; } = "cpu";
    public string LastTranscript { get; private set; } = "";
    public string? LastError { get; private set; }
    public bool LaunchAtLogin { get; private set; }
    public bool IsInstalling { get; private set; }
    public string InstallLog { get; private set; } = "";
    public string Section { get; private set; } = "calls";
    public string? LastCapturePath { get; private set; }
    public double LastCaptureDuration { get; private set; }
    public long LastCaptureBytes { get; private set; }
    public string DebugResult { get; private set; } = "";
    public bool MicrophoneAllowed { get; private set; }
    public bool HotkeyAllowed { get; private set; }
    public bool SystemAudioAllowed { get; private set; }
    public string? HotkeyFailure => _hotkeys.Failure;
    public bool SuppressCallSelection { get; private set; }
    bool DidFinishOnboarding { get; set; }

    public string PhaseTitle => Phase switch
    {
        Phase.Dictating => _handsFree ? "Диктовка без удержания" : "Диктовка",
        Phase.Transcribing => "Распознавание диктовки",
        Phase.RecordingCall => "Запись звонка",
        Phase.TranscribingCall => "Расшифровка звонка",
        _ => "Ожидание"
    };

    public string ModelTitle => ModelKind switch
    {
        ModelKind.Starting => "Запуск распознавания…",
        ModelKind.Missing => "Модель не установлена",
        ModelKind.Loading => "Загрузка модели…",
        ModelKind.Ready => "GigaAM v3 готова",
        _ => "Ошибка модели"
    };

    public string HotkeyText => IsCapturingHotkey ? "Нажмите сочетание…" : Hotkey.DisplayName;
    public string DictationButtonText => Phase == Phase.Dictating ? "Остановить диктовку" : $"Диктовка ({Hotkey.DisplayName})";
    public string CallButtonText => Phase == Phase.RecordingCall ? "Остановить и расшифровать" : "Начать запись звонка";
    public bool CanInstall => !IsInstalling && Phase == Phase.Idle && ModelKind is not (ModelKind.Starting or ModelKind.Loading);
    public bool CanDictate => Phase == Phase.Dictating || (Phase == Phase.Idle && ModelReady && MicrophoneAllowed);
    public bool CanToggleCall => Phase == Phase.RecordingCall || (Phase == Phase.Idle && ModelReady);
    public bool ShouldCancelWithEscape => Phase == Phase.Dictating || _armed;
    public string CaptureSummary => string.IsNullOrEmpty(LastCapturePath)
        ? "ещё нет"
        : $"{LastCaptureDuration.ToString("0.00")} с, {(LastCaptureBytes / 1024.0).ToString("0.0")} КБ, {LastCapturePath}";

    public void Bootstrap()
    {
        if (_bootstrapped)
            return;
        _bootstrapped = true;
        LoadSettings();
        foreach (var entry in HistoryStore.Load())
            History.Add(entry);
        ReloadCalls();
        RefreshDevices();
        RefreshLogin();
        RefreshPermissions();
        StartWorker();
        Note("GigaTTS запущен. log=" + AppLog.Shared.FilePath);
        if (!HotkeyAllowed && HotkeyFailure is { Length: > 0 } failure)
        {
            LastError = failure;
            Raise(nameof(LastError));
            Note(failure, "WARN");
        }
        if (!DidFinishOnboarding || !MicrophoneAllowed)
            Dispatcher.UIThread.Post(() => OnNeedOnboarding?.Invoke());
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        try { _installCancel?.Cancel(); } catch { /* Already cancelled. */ }
        _armGeneration++;
        _callTimer.Stop();
        if (Phase == Phase.RecordingCall && _activeCall != null)
        {
            var mic = _microphone.Duration;
            var system = _loopback.Duration;
            _microphone.Stop();
            _loopback.Stop();
            _activeCall.Duration = Math.Max(mic, Math.Max(system, _callElapsed));
            _activeCall.HasSystemAudio = system > 0.3;
            CallStore.Save(_activeCall);
        }
        else if (Phase == Phase.Dictating)
        {
            TryDelete(_microphone.Stop());
        }
        _overlay.Hide();
        _worker.Stop();
        _hotkeys.Stop();
    }

    public void RefreshPermissions()
    {
        MicrophoneAllowed = QueryMicrophone();
        if (!_hotkeys.IsRunning)
            _hotkeys.Start();
        HotkeyAllowed = _hotkeys.IsRunning;
        SystemAudioAllowed = QuerySystemAudio();
        Raise(nameof(MicrophoneAllowed));
        Raise(nameof(HotkeyAllowed));
        Raise(nameof(SystemAudioAllowed));
        Raise(nameof(HotkeyFailure));
        Touch();
    }

    public void RefreshDevices()
    {
        var selected = SelectedDeviceId;
        Devices.Clear();
        foreach (var device in ListInputs())
            Devices.Add(device);
        if (Devices.All(device => device.Id != selected))
            SelectedDeviceId = "";
    }

    public void RefreshLogin()
    {
        try
        {
            LaunchAtLogin = PlatformServices.LaunchAtLogin();
        }
        catch
        {
            LaunchAtLogin = false;
        }
        Raise(nameof(LaunchAtLogin));
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            PlatformServices.SetLaunchAtLogin(enabled);
        }
        catch (Exception ex)
        {
            LastError = "Автозапуск недоступен. " + ex.Message;
            Raise(nameof(LastError));
        }
        RefreshLogin();
    }

    public void SetDeviceId(string id)
    {
        if (SelectedDeviceId == id)
            return;
        SelectedDeviceId = id;
        SaveSettings();
        Raise(nameof(SelectedDeviceId));
    }

    public void SetModelDevice(string device)
    {
        var normalized = device == "cuda" ? "cuda" : "cpu";
        if (normalized == ModelDevice || Phase != Phase.Idle)
            return;
        ModelDevice = normalized;
        SaveSettings();
        Raise(nameof(ModelDevice));
        StartWorker();
    }

    public void ReloadModel()
    {
        if (Phase == Phase.Idle)
            StartWorker();
    }

    public void BeginHotkeyCapture()
    {
        if (Phase != Phase.Idle)
            return;
        IsCapturingHotkey = true;
        _hotkeys.ResetCapture();
        Touch();
    }

    public void CancelHotkeyCapture()
    {
        IsCapturingHotkey = false;
        Touch();
    }

    public void ApplyHotkey(Hotkey hotkey)
    {
        Hotkey = hotkey;
        IsCapturingHotkey = false;
        SaveSettings();
        Touch();
    }

    public void ResetHotkey() => ApplyHotkey(Hotkey.RightAlt);

    public void FinishOnboarding()
    {
        DidFinishOnboarding = true;
        SaveSettings();
    }

    public void OpenSettings() => OnOpenSettings?.Invoke();

    public void ShowSection(string section)
    {
        Section = section;
        Raise(nameof(Section));
        OnOpenCalls?.Invoke();
    }

    public void InstallWorker()
    {
        if (!CanInstall)
            return;
        _generation++;
        _worker.Stop();
        IsInstalling = true;
        InstallLog = "";
        Raise(nameof(IsInstalling));
        Raise(nameof(InstallLog));
        SetModel(ModelKind.Loading, "Установка распознавания…");
        LastError = null;
        Raise(nameof(LastError));
        _installCancel?.Cancel();
        _installCancel = new CancellationTokenSource();
        var token = _installCancel.Token;
        _ = RunInstall(token);
    }

    public void RequestMicrophone() => PlatformServices.RequestMicrophone();
    public void RequestSystemAudio() => PlatformServices.RequestSystemAudio();

    public void RetryHotkey()
    {
        var previous = HotkeyFailure;
        _hotkeys.Start();
        RefreshPermissions();
        if (HotkeyAllowed)
        {
            if (LastError != null && LastError == previous)
            {
                LastError = null;
                Raise(nameof(LastError));
            }
            return;
        }
        LastError = string.IsNullOrEmpty(HotkeyFailure)
            ? "Не удалось включить глобальную клавишу. Перезапустите GigaTTS."
            : HotkeyFailure;
        Raise(nameof(LastError));
    }

    public void HotkeyPressed()
    {
        if (Phase == Phase.Dictating && _handsFree)
        {
            _suppressRelease = true;
            StopDictation();
            return;
        }
        if (Phase == Phase.Dictating)
            return;
        if (Phase != Phase.Idle)
        {
            var message = Phase is Phase.RecordingCall or Phase.TranscribingCall
                ? "Сначала остановите запись звонка"
                : "Подождите, идёт распознавание";
            Note("Хоткей проигнорирован: " + message, "WARN");
            Flash(message);
            return;
        }
        if (!ModelReady)
        {
            Note("Хоткей проигнорирован: " + ModelTitle, "WARN");
            Flash(ModelTitle);
            if (ModelKind == ModelKind.Missing)
                OnNeedOnboarding?.Invoke();
            return;
        }
        if (!MicrophoneAllowed)
        {
            Flash("Нет доступа к микрофону");
            OnNeedOnboarding?.Invoke();
            return;
        }
        _armed = true;
        var generation = ++_armGeneration;
        _ = Task.Delay(300).ContinueWith(_ => OnUi(() =>
        {
            if (generation != _armGeneration)
                return;
            _armed = false;
            StartDictation(false);
        }));
    }

    public void HotkeyReleased()
    {
        if (_suppressRelease)
        {
            _suppressRelease = false;
            return;
        }
        var wasArmed = _armed;
        _armGeneration++;
        _armed = false;
        if (wasArmed && Phase == Phase.Idle)
            return;
        if (Phase == Phase.Dictating && !_handsFree)
            StopDictation();
    }

    public void ToggleHandsFree()
    {
        if (Phase == Phase.Dictating)
        {
            StopDictation();
            return;
        }
        if (Phase != Phase.Idle)
        {
            Flash("Сначала завершите текущую запись");
            return;
        }
        if (!ModelReady)
        {
            Flash(ModelTitle);
            return;
        }
        if (!MicrophoneAllowed)
        {
            Flash("Нет доступа к микрофону");
            OnNeedOnboarding?.Invoke();
            return;
        }
        StartDictation(true);
    }

    public void CancelDictation()
    {
        _armGeneration++;
        _armed = false;
        _suppressRelease = false;
        if (Phase == Phase.Dictating)
            TryDelete(_microphone.Stop());
        if (Phase is not (Phase.RecordingCall or Phase.TranscribingCall))
        {
            SetPhase(Phase.Idle);
            _overlay.Hide();
        }
    }

    public void CopyLastTranscript()
    {
        if (string.IsNullOrEmpty(LastTranscript))
            return;
        if (!PlatformClipboard.TrySetText(LastTranscript))
        {
            LastError = "Не удалось скопировать текст.";
            Raise(nameof(LastError));
        }
    }

    public void InsertHistory(DictationEntry entry)
    {
        LastTranscript = entry.Text;
        Raise(nameof(LastTranscript));
        ReportInsert(TextInserter.Insert(entry.Text));
    }

    public void ToggleCall()
    {
        if (Phase == Phase.RecordingCall)
        {
            StopCall();
            return;
        }
        if (Phase != Phase.Idle)
        {
            Flash("Сначала завершите диктовку");
            return;
        }
        if (!ModelReady)
        {
            Flash(ModelTitle);
            return;
        }
        RefreshPermissions();
        if (!MicrophoneAllowed)
        {
            Flash("Нет доступа к микрофону");
            OnNeedOnboarding?.Invoke();
            return;
        }
        BeginCall();
    }

    public void DeleteCall(CallRecord record)
    {
        CallStore.Delete(record);
        ReloadCalls();
    }

    public void Retranscribe(CallRecord record)
    {
        if (Phase != Phase.Idle)
        {
            Flash("Дождитесь окончания текущей операции");
            return;
        }
        if (!ModelReady)
        {
            Flash(ModelTitle);
            return;
        }
        SetPhase(Phase.TranscribingCall);
        _overlay.ShowTranscribing();
        _ = TranscribeCall(record, _generation);
    }

    public void RetranscribeLastCapture()
    {
        if (string.IsNullOrEmpty(LastCapturePath))
        {
            Note("Нет сохранённой записи для повторного распознавания.", "WARN");
            return;
        }
        TranscribeFile(LastCapturePath);
    }

    public void TranscribeFile(string path)
    {
        if (Phase != Phase.Idle)
        {
            Flash("Дождитесь окончания текущей операции");
            return;
        }
        if (!ModelReady)
        {
            Flash(ModelTitle);
            return;
        }
        LastCapturePath = path;
        LastCaptureBytes = FileSize(path);
        LastCaptureDuration = 0;
        Raise(nameof(CaptureSummary));
        SetPhase(Phase.Transcribing);
        _overlay.ShowTranscribing();
        Note($"Ручное распознавание. bytes={LastCaptureBytes} file={path}");
        var generation = _generation;
        _ = FinishManual(path, generation);
    }

    public void InsertDebugResult()
    {
        var text = DebugResult.Trim();
        if (text.Length == 0)
            return;
        Remember(text);
        ReportInsert(TextInserter.Insert(text));
    }

    async Task RunInstall(CancellationToken cancel)
    {
        try
        {
            await WorkerInstaller.Install(text => OnUi(() =>
            {
                if (!_shutdown)
                    AppendInstall(text);
            }), cancel);
            OnUi(() =>
            {
                if (_shutdown)
                    return;
                IsInstalling = false;
                Raise(nameof(IsInstalling));
                AppendInstall("\nРаспознавание установлено.\n");
                ReloadModel();
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            OnUi(() =>
            {
                if (_shutdown)
                    return;
                IsInstalling = false;
                Raise(nameof(IsInstalling));
                SetModel(ModelKind.Failed, "Установка прервана.");
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (_shutdown)
                    return;
                IsInstalling = false;
                Raise(nameof(IsInstalling));
                SetModel(ModelKind.Failed, ex.Message);
                LastError = ex.Message;
                Raise(nameof(LastError));
                Note("Установка не удалась: " + ex.Message, "ERROR");
            });
        }
    }

    void StartDictation(bool handsFree)
    {
        if (Phase != Phase.Idle || !ModelReady)
            return;
        var path = Path.Combine(Path.GetTempPath(), "gigatts-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            _microphone.StartMicrophone(path, SelectedDeviceId);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Raise(nameof(LastError));
            Note("Не удалось начать запись: " + ex.Message, "ERROR");
            Flash(ex.Message);
            return;
        }
        SetPhase(Phase.Dictating, handsFree);
        _overlay.ShowDictation();
        Note($"Запись начата. handsFree={handsFree} device={(SelectedDeviceId.Length == 0 ? "system" : SelectedDeviceId)} file={path}");
    }

    void StopDictation()
    {
        _armGeneration++;
        _armed = false;
        if (Phase != Phase.Dictating)
            return;
        var duration = _microphone.Duration;
        var path = _microphone.Stop();
        if (string.IsNullOrEmpty(path))
        {
            SetPhase(Phase.Idle);
            _overlay.Hide();
            Note("Запись остановилась без файла.", "WARN");
            return;
        }
        var bytes = FileSize(path);
        Note($"Запись остановлена. duration_s={duration:0.000} bytes={bytes} file={path}");
        if (duration < 0.25)
        {
            TryDelete(path);
            SetPhase(Phase.Idle);
            _overlay.Hide();
            Note("Запись короче 0,25 с, распознавание пропущено.", "WARN");
            return;
        }
        SetPhase(Phase.Transcribing);
        _overlay.ShowTranscribing();
        var generation = _generation;
        _ = FinishDictationFile(path, duration, generation);
    }

    async Task FinishDictationFile(string path, double duration, int generation)
    {
        try
        {
            var text = await RequestText(path);
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                KeepLast(path, duration);
                FinishDictation(text);
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                KeepLast(path, duration);
                SetPhase(Phase.Idle);
                _overlay.Hide();
                LastError = ex.Message;
                Raise(nameof(LastError));
                Note("Ошибка распознавания: " + ex.Message, "ERROR");
                Flash(ex.Message);
                ShowSection("debug");
            });
        }
    }

    void FinishDictation(string text)
    {
        SetPhase(Phase.Idle);
        _overlay.Hide();
        DebugResult = text;
        Raise(nameof(DebugResult));
        if (text.Length == 0)
        {
            LastError = "Речь не распознана";
            Raise(nameof(LastError));
            Note($"Речь не распознана. duration_s={LastCaptureDuration:0.000} bytes={LastCaptureBytes} file={LastCapturePath}", "WARN");
            Flash("Речь не распознана");
            ShowSection("debug");
            return;
        }
        Note($"Распознано. chars={text.Length} text={TrimLog(text)}");
        Remember(text);
        var outcome = TextInserter.Insert(text);
        if (outcome == InsertOutcome.Inserted)
            Note("Текст вставлен в активное поле.");
        ReportInsert(outcome);
    }

    void BeginCall()
    {
        if (Phase != Phase.Idle)
            return;
        var record = new CallRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now
        };
        CallStore.Save(record);
        try
        {
            _microphone.StartMicrophone(record.MicPath, SelectedDeviceId);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Raise(nameof(LastError));
            Flash(ex.Message);
            CallStore.Delete(record);
            return;
        }
        _activeCall = record;
        SetPhase(Phase.RecordingCall);
        _callElapsed = 0;
        _overlay.ShowCall(Format.Minutes(0));
        _callTimer.Start();
        try
        {
            _loopback.StartLoopback(record.SystemPath);
        }
        catch (Exception ex)
        {
            LastError = "Системный звук недоступен: " + ex.Message + ". Пишется только микрофон.";
            Raise(nameof(LastError));
            RefreshPermissions();
        }
    }

    void StopCall()
    {
        if (Phase != Phase.RecordingCall || _activeCall == null)
            return;
        _callTimer.Stop();
        var record = _activeCall;
        var mic = _microphone.Duration;
        var system = _loopback.Duration;
        _microphone.Stop();
        _loopback.Stop();
        record.Duration = Math.Max(mic, Math.Max(system, _callElapsed));
        record.HasSystemAudio = system > 0.3;
        CallStore.Save(record);
        ReloadCalls();
        SetPhase(Phase.TranscribingCall);
        _overlay.ShowTranscribing();
        _ = TranscribeCall(record, _generation);
    }

    async Task TranscribeCall(CallRecord record, int generation)
    {
        try
        {
            var segments = await RequestCall(record);
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                FinishCall(record.Id, segments);
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                SetPhase(Phase.Idle);
                _overlay.Hide();
                LastError = ex.Message;
                Raise(nameof(LastError));
                Flash("Не удалось расшифровать звонок");
                ReloadCalls();
                ShowSection("calls");
            });
        }
    }

    void FinishCall(Guid id, List<TranscriptSegment> segments)
    {
        SetPhase(Phase.Idle);
        _overlay.Hide();
        _activeCall = null;
        var record = CallStore.Load().FirstOrDefault(item => item.Id == id);
        if (record != null)
        {
            record.Segments = segments;
            CallStore.Save(record);
        }
        ReloadCalls();
        if (segments.Count == 0)
            Flash("В записи не найдена речь");
        ShowSection("calls");
    }

    async Task FinishManual(string path, int generation)
    {
        try
        {
            var text = await RequestText(path);
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                SetPhase(Phase.Idle);
                _overlay.Hide();
                DebugResult = text;
                Raise(nameof(DebugResult));
                if (text.Length == 0)
                {
                    LastError = "Речь не распознана";
                    Raise(nameof(LastError));
                    Note("Ручное распознавание вернуло пустой текст. file=" + path, "WARN");
                }
                else
                {
                    LastError = null;
                    Raise(nameof(LastError));
                    Note($"Ручное распознавание. chars={text.Length} text={TrimLog(text)}");
                }
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                SetPhase(Phase.Idle);
                _overlay.Hide();
                LastError = ex.Message;
                Raise(nameof(LastError));
                Note("Ошибка ручного распознавания: " + ex.Message, "ERROR");
            });
        }
    }

    void StartWorker()
    {
        _generation++;
        _worker.Stop();
        _logTail = "";
        _runningDevice = "";
        var located = WorkerLocator.Locate();
        if (located == null)
        {
            SetModel(ModelKind.Missing, "Нажмите «Установить распознавание».");
            Note("Распознавание не установлено.", "WARN");
            return;
        }
        Note($"Запуск воркера. python={located.Value.Python} script={located.Value.Script}");
        SetModel(ModelKind.Loading, "Загрузка v3_e2e_rnnt…");
        var generation = _generation;
        _ = StartAndLoad(located.Value, generation);
    }

    async Task StartAndLoad(WorkerLocator.Located located, int generation)
    {
        WorkerRuntime.SyncBundledScript();
        try
        {
            if (await WorkerRuntime.EnsureTruststore(located.Python))
                OnUi(() => Note("В окружение добавлен пакет truststore."));
        }
        catch
        {
            // Offline load of an already downloaded model must still proceed.
        }
        if (_shutdown || generation != _generation)
            return;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            if (_shutdown || generation != _generation)
            {
                started.TrySetResult(false);
                return;
            }
            try
            {
                _worker.Start(located.Python, WorkerRuntime.ScriptToLaunch(located.Script), ModelDevice);
                started.TrySetResult(true);
            }
            catch (Exception ex)
            {
                SetModel(ModelKind.Failed, ex.Message);
                LastError = ex.Message;
                Raise(nameof(LastError));
                Note("Не удалось запустить воркер: " + ex.Message, "ERROR");
                started.TrySetResult(false);
            }
        });
        if (!await started.Task || _shutdown || generation != _generation)
            return;
        await LoadModel(generation);
    }

    async Task LoadModel(int generation)
    {
        try
        {
            using var _ = await _worker.RequestAsync(new JsonObject
            {
                ["cmd"] = "load",
                ["model"] = "v3_e2e_rnnt"
            });
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                var label = _runningDevice == "cuda" ? "NVIDIA GPU" : ModelDevice == "cuda" && _runningDevice != "cpu" ? "NVIDIA GPU" : "CPU";
                SetModel(ModelKind.Ready, label);
                LastError = null;
                Raise(nameof(LastError));
                Note("Модель готова. device=" + label);
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (generation != _generation)
                    return;
                LastError = ex.Message;
                Raise(nameof(LastError));
                var tail = _logTail.Length > 280 ? _logTail[^280..] : _logTail;
                SetModel(ModelKind.Failed, ModelErrorText.MergeFailure(tail, ex.Message));
                Note("Модель не загрузилась: " + ex.Message + " " + tail, "ERROR");
            });
        }
    }

    async Task<string> RequestText(string path)
    {
        using var document = await _worker.RequestAsync(new JsonObject
        {
            ["cmd"] = "transcribe",
            ["path"] = path
        });
        return document.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
    }

    async Task<List<TranscriptSegment>> RequestCall(CallRecord record)
    {
        var body = new JsonObject
        {
            ["cmd"] = "transcribe_call",
            ["mic"] = record.MicPath
        };
        if (File.Exists(record.SystemPath))
            body["system"] = record.SystemPath;
        using var document = await _worker.RequestAsync(body);
        return ParseSegments(document.RootElement);
    }

    void Remember(string text)
    {
        LastTranscript = text;
        History.Insert(0, new DictationEntry { Id = Guid.NewGuid(), Date = DateTime.Now, Text = text });
        while (History.Count > 40)
            History.RemoveAt(History.Count - 1);
        HistoryStore.Save(History);
        Raise(nameof(LastTranscript));
        Touch();
    }

    void ReportInsert(InsertOutcome outcome)
    {
        switch (outcome)
        {
            case InsertOutcome.Inserted:
                LastError = null;
                break;
            case InsertOutcome.Secure:
                LastError = "Поле ввода защищено. Фраза есть в меню.";
                Flash("Поле защищено — текст в меню");
                break;
            default:
                LastError = TextInserter.LastProblem ?? "Не удалось вставить текст. Он сохранён в меню.";
                Flash("Не удалось вставить текст");
                break;
        }
        Raise(nameof(LastError));
    }

    void KeepLast(string path, double duration)
    {
        var directory = Path.Combine(AppPaths.Support, "debug");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "last-dictation.wav");
        try
        {
            if (File.Exists(destination))
                File.Delete(destination);
            File.Copy(path, destination);
            if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
                TryDelete(path);
            LastCapturePath = destination;
        }
        catch (Exception ex)
        {
            LastCapturePath = path;
            Note("Не удалось сохранить копию записи: " + ex.Message, "WARN");
        }
        LastCaptureDuration = duration;
        LastCaptureBytes = FileSize(LastCapturePath);
        Raise(nameof(CaptureSummary));
    }

    void ReloadCalls()
    {
        var records = CallStore.Load();
        SuppressCallSelection = true;
        Calls.Clear();
        foreach (var record in records)
            Calls.Add(record);
        SuppressCallSelection = false;
        CallsReloaded?.Invoke(this, EventArgs.Empty);
    }

    void AppendInstall(string text)
    {
        var next = InstallLog + text;
        InstallLog = next.Length > 12_000 ? next[^12_000..] : next;
        Raise(nameof(InstallLog));
        var line = text.Trim();
        if (line.Length == 0)
            return;
        SetStatus(line.Length > 180 ? line[^180..] : line);
        Note("Установка: " + line.Replace('\n', ' '));
    }

    void SetPhase(Phase phase, bool handsFree = false)
    {
        _handsFree = handsFree;
        Phase = phase;
        Raise(nameof(Phase));
        Raise(nameof(HandsFree));
        Touch();
    }

    void SetModel(ModelKind kind, string? detail = null)
    {
        ModelKind = kind;
        Raise(nameof(ModelKind));
        Raise(nameof(ModelReady));
        if (detail != null)
            StatusDetail = kind == ModelKind.Failed
                ? ModelErrorText.WithClockHint(detail, OperatingSystem.IsWindows())
                : detail;
        Raise(nameof(StatusDetail));
        Touch();
    }

    void SetStatus(string detail)
    {
        StatusDetail = detail;
        Raise(nameof(StatusDetail));
        Touch();
    }

    void Absorb(string text)
    {
        _logTail += text;
        if (_logTail.Length > 4000)
            _logTail = _logTail[^4000..];
        var marker = _logTail.LastIndexOf("model ready:", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var ready = _logTail[marker..];
            var previous = _runningDevice;
            if (ready.Contains("cuda", StringComparison.Ordinal))
                _runningDevice = "cuda";
            else if (ready.Contains("cpu", StringComparison.Ordinal))
                _runningDevice = "cpu";
            if (ModelKind == ModelKind.Ready && _runningDevice.Length > 0 && _runningDevice != previous)
                SetStatus(_runningDevice == "cuda" ? "NVIDIA GPU" : "CPU");
        }
        if (ModelKind != ModelKind.Loading)
            return;
        var lowered = text.ToLowerInvariant();
        if (lowered.Contains("download") || text.Contains("iB", StringComparison.Ordinal) || text.Contains('%'))
            SetStatus("Скачивание весов GigaAM…");
    }

    void Touch()
    {
        Raise(nameof(PhaseTitle));
        Raise(nameof(ModelTitle));
        Raise(nameof(HotkeyText));
        Raise(nameof(DictationButtonText));
        Raise(nameof(CallButtonText));
        Raise(nameof(CanInstall));
        Raise(nameof(CanDictate));
        Raise(nameof(CanToggleCall));
        Raise(nameof(CaptureSummary));
        QueueMenu();
    }

    void QueueMenu()
    {
        if (_menuQueued)
            return;
        _menuQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _menuQueued = false;
            MenuChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    void LoadSettings()
    {
        var data = SettingsStore.Load();
        Hotkey = data.VirtualKey == 0
            ? Hotkey.RightAlt
            : new Hotkey((ushort)data.VirtualKey, data.IsModifier, (KeyMods)data.Modifiers);
        SelectedDeviceId = data.DeviceId ?? "";
        ModelDevice = data.ModelDevice == "cuda" ? "cuda" : "cpu";
        DidFinishOnboarding = data.DidFinishOnboarding;
    }

    void SaveSettings()
    {
        SettingsStore.Save(new SettingsData
        {
            VirtualKey = Hotkey.VirtualKey,
            Modifiers = (uint)Hotkey.Modifiers,
            IsModifier = Hotkey.IsModifier,
            DeviceId = SelectedDeviceId,
            ModelDevice = ModelDevice,
            DidFinishOnboarding = DidFinishOnboarding
        });
    }

    static bool QueryMicrophone() =>
        OperatingSystem.IsWindows() ? WindowsMicrophone() : LinuxPulse.HasMicrophone();

    static bool WindowsMicrophone() => PlatformServices.WindowsMicrophoneAllowed();

    static bool QuerySystemAudio() =>
        OperatingSystem.IsWindows() ? WindowsSystemAudio() : LinuxPulse.HasMonitor();

    static bool WindowsSystemAudio() => AudioDevices.HasRenderDevice();

    static List<AudioDevice> ListInputs() =>
        OperatingSystem.IsWindows() ? WindowsInputs() : LinuxPulse.Inputs();

    static List<AudioDevice> WindowsInputs() => AudioDevices.Inputs();

    static List<TranscriptSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptSegment>();
        if (!root.TryGetProperty("segments", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return segments;
        foreach (var row in rows.EnumerateArray())
        {
            var text = row.TryGetProperty("text", out var textProperty) ? (textProperty.GetString() ?? "").Trim() : "";
            if (text.Length == 0)
                continue;
            segments.Add(new TranscriptSegment
            {
                Start = row.TryGetProperty("start", out var start) && start.TryGetDouble(out var startValue) ? startValue : 0,
                End = row.TryGetProperty("end", out var end) && end.TryGetDouble(out var endValue) ? endValue : 0,
                Speaker = row.TryGetProperty("speaker", out var speaker) ? speaker.GetString() ?? "me" : "me",
                Text = text
            });
        }
        return segments;
    }

    static long FileSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return 0;
        return new FileInfo(path).Length;
    }

    static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* The temp file can stay. */ }
    }

    static string TrimLog(string text) => text.Length > 300 ? text[..300] : text;

    void Note(string message, string level = "INFO") => AppLog.Shared.Write(level, message);

    void Flash(string text) => _overlay.ShowMessage(text);

    void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
