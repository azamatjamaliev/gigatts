import AppKit
import AVFoundation
import ServiceManagement
import UniformTypeIdentifiers

final class AppState: ObservableObject, @unchecked Sendable {
    @Published var phase: SessionPhase = .idle
    @Published var modelStatus: ModelStatus = .starting
    @Published var permissions = PermissionSnapshot()
    @Published var hotkey = Hotkey.rightOption
    @Published var isCapturingHotkey = false
    @Published var inputDevices: [AudioInputDevice] = []
    @Published var selectedDeviceUID = ""
    @Published var modelDevice = "cpu"
    @Published var dictationHistory: [DictationEntry] = []
    @Published var calls: [CallRecord] = []
    @Published var lastTranscript = ""
    @Published var lastError: String?
    @Published var statusDetail = ""
    @Published var launchAtLogin = false
    @Published var callElapsed: TimeInterval = 0
    @Published var isInstalling = false
    @Published var installLog = ""
    @Published var mainSection: MainSection = .calls
    @Published var lastCaptureURL: URL?
    @Published var lastCaptureDuration: TimeInterval = 0
    @Published var lastCaptureBytes: Int64 = 0
    @Published var debugResult = ""

    var onNeedOnboarding: (() -> Void)?
    var onOpenCalls: (() -> Void)?
    var onOpenSettings: (() -> Void)?

    let worker = WorkerClient()
    let hotkeyMonitor = HotkeyMonitor()
    let overlay = OverlayController()
    let microphone = MicrophoneRecorder()
    let systemAudio = SystemAudioRecorder()

    private var didBootstrap = false
    private var didShutdown = false
    private var armWork: DispatchWorkItem?
    private var suppressRelease = false
    private var activeCall: CallRecord?
    private var callTimer: Timer?
    private var workerGeneration = 0
    private var logTail = ""

    var phaseTitle: String {
        switch phase {
        case .idle:
            return "Ожидание"
        case .dictating(let handsFree):
            return handsFree ? "Диктовка без удержания" : "Диктовка"
        case .transcribing:
            return "Распознавание диктовки"
        case .recordingCall:
            return "Запись звонка"
        case .transcribingCall:
            return "Расшифровка звонка"
        }
    }

    func note(_ message: String, level: String = "INFO") {
        AppLog.shared.write(level, message)
    }

    var symbolName: String {
        switch phase {
        case .dictating:
            return "mic.fill"
        case .transcribing, .transcribingCall:
            return "hourglass"
        case .recordingCall:
            return "record.circle"
        case .idle:
            switch modelStatus {
            case .failed, .missingRuntime:
                return "exclamationmark.triangle.fill"
            default:
                return "waveform"
            }
        }
    }

    var shouldCancelWithEscape: Bool {
        if case .dictating = phase { return true }
        return armWork != nil
    }

    var canStartDictation: Bool {
        phase == .idle && modelStatus.isReady && permissions.microphone
    }

    func bootstrap() {
        guard !didBootstrap else { return }
        didBootstrap = true
        NSApp.setActivationPolicy(.accessory)
        loadPreferences()
        dictationHistory = HistoryStore.load()
        calls = CallStore.load()
        refreshDevices()
        refreshLoginItem()
        hotkeyMonitor.appState = self
        refreshPermissions()
        worker.onLog = { [weak self] text in
            self?.absorbLog(text)
            self?.note("Воркер: \(text)", level: "WORKER")
        }
        worker.onExit = { [weak self] code in
            guard let self, !self.didShutdown else { return }
            self.note("Процесс распознавания завершился. code=\(code)", level: "ERROR")
            if self.workerGeneration > 0, case .loading = self.modelStatus {
                return
            }
            if case .ready = self.modelStatus {
                self.modelStatus = .failed("Процесс распознавания остановился (код \(code)).")
            }
        }
        NotificationCenter.default.addObserver(
            forName: NSApplication.willTerminateNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.shutdown()
        }
        startWorker()
        note("GigaПисец запущен. log=\(AppLog.shared.fileURL.path)")
        let finished = UserDefaults.standard.bool(forKey: DefaultsKey.onboarding)
        if !finished || !permissions.readyForDictation {
            DispatchQueue.main.async { [weak self] in
                self?.onNeedOnboarding?()
            }
        }
    }

    func shutdown() {
        guard !didShutdown else { return }
        didShutdown = true
        callTimer?.invalidate()
        if case .recordingCall = phase, var call = activeCall {
            let micDuration = microphone.duration
            let systemDuration = systemAudio.duration
            _ = microphone.stop()
            _ = systemAudio.stop()
            call.duration = max(micDuration, systemDuration, callElapsed)
            call.hasSystemAudio = systemDuration > 0.3
            CallStore.save(call)
        } else if case .dictating = phase, let url = microphone.stop() {
            try? FileManager.default.removeItem(at: url)
        }
        overlay.hide()
        worker.stop()
        hotkeyMonitor.stop()
    }

    func refreshPermissions() {
        permissions = Permissions.snapshot()
        if !hotkeyMonitor.isRunning {
            hotkeyMonitor.start()
        }
        if hotkeyMonitor.isRunning {
            permissions.inputMonitoring = true
        } else {
            note("Хоткей недоступен: нет мониторинга ввода (Accessibility/Input Monitoring).", level: "WARN")
        }
    }

    func refreshDevices() {
        inputDevices = AudioDevices.inputs()
    }

    func loadPreferences() {
        let defaults = UserDefaults.standard
        if defaults.object(forKey: DefaultsKey.keyCode) != nil {
            hotkey = Hotkey(
                keyCode: UInt16(defaults.integer(forKey: DefaultsKey.keyCode)),
                modifiers: UInt64(bitPattern: Int64(defaults.integer(forKey: DefaultsKey.modifiers))),
                isModifier: defaults.object(forKey: DefaultsKey.isModifier) == nil
                    ? true
                    : defaults.bool(forKey: DefaultsKey.isModifier)
            )
        }
        selectedDeviceUID = defaults.string(forKey: DefaultsKey.deviceUID) ?? ""
        let storedDevice = defaults.string(forKey: DefaultsKey.modelDevice) ?? "cpu"
        modelDevice = storedDevice == "mps" ? "mps" : "cpu"
    }

    func setDeviceUID(_ uid: String) {
        selectedDeviceUID = uid
        UserDefaults.standard.set(uid, forKey: DefaultsKey.deviceUID)
    }

    func setModelDevice(_ device: String) {
        let normalized = device == "mps" ? "mps" : "cpu"
        guard normalized != modelDevice else { return }
        modelDevice = normalized
        UserDefaults.standard.set(normalized, forKey: DefaultsKey.modelDevice)
        guard phase == .idle else { return }
        startWorker()
    }

    func reloadModel() {
        guard phase == .idle else { return }
        startWorker()
    }

    func beginHotkeyCapture() {
        isCapturingHotkey = true
        hotkeyMonitor.resetCapture()
    }

    func applyCapturedHotkey(_ captured: Hotkey) {
        hotkey = captured
        isCapturingHotkey = false
        let defaults = UserDefaults.standard
        defaults.set(Int(hotkey.keyCode), forKey: DefaultsKey.keyCode)
        defaults.set(Int(bitPattern: UInt(hotkey.modifiers)), forKey: DefaultsKey.modifiers)
        defaults.set(hotkey.isModifier, forKey: DefaultsKey.isModifier)
    }

    func resetHotkey() {
        isCapturingHotkey = false
        applyCapturedHotkey(.rightOption)
    }

    func finishOnboarding() {
        UserDefaults.standard.set(true, forKey: DefaultsKey.onboarding)
    }

    func refreshLoginItem() {
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                if SMAppService.mainApp.status != .enabled {
                    try SMAppService.mainApp.register()
                }
            } else if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            lastError = "Автозапуск недоступен, пока приложение не лежит в /Applications. \(error.localizedDescription)"
        }
        refreshLoginItem()
    }

    /// Menu-bar (LSUIElement / .accessory) apps must become regular before a window can reliably appear.
    func revealForUI() {
        if NSApp.activationPolicy() != .regular {
            NSApp.setActivationPolicy(.regular)
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    func openSettings() {
        revealForUI()
        if let onOpenSettings {
            onOpenSettings()
        } else {
            // Fallback if the SwiftUI openWindow callback is not wired yet.
            NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
        }
        // openWindow is asynchronous; retry briefly until the settings window exists.
        for delay in [0.0, 0.05, 0.15, 0.35] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.orderFrontSettingsWindow()
            }
        }
    }

    private func orderFrontSettingsWindow() {
        let matches = NSApp.windows.filter { window in
            let title = window.title
            let autosave = window.frameAutosaveName
            let identifier = window.identifier?.rawValue ?? ""
            return title == "Настройки"
                || title.localizedCaseInsensitiveContains("settings")
                || autosave.localizedCaseInsensitiveContains("settings")
                || identifier.localizedCaseInsensitiveContains("settings")
        }
        guard !matches.isEmpty else { return }
        revealForUI()
        for window in matches {
            window.collectionBehavior.insert(.moveToActiveSpace)
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
        }
    }

    var canInstallWorker: Bool {
        if isInstalling || phase != .idle { return false }
        switch modelStatus {
        case .starting, .loading:
            return false
        default:
            return true
        }
    }

    func installWorker() {
        guard canInstallWorker else { return }
        workerGeneration += 1
        worker.stop()
        isInstalling = true
        installLog = ""
        modelStatus = .loading
        statusDetail = "Установка распознавания…"
        lastError = nil
        Task {
            do {
                try await WorkerInstaller.install { text in
                    DispatchQueue.main.async {
                        self.appendInstallLog(text)
                    }
                }
                DispatchQueue.main.async {
                    self.isInstalling = false
                    self.appendInstallLog("\nРаспознавание установлено.\n")
                    self.reloadModel()
                }
            } catch {
                DispatchQueue.main.async {
                    self.isInstalling = false
                    let message = error.localizedDescription
                    self.modelStatus = .failed(message)
                    self.lastError = message
                    self.statusDetail = message
                    self.note("Установка не удалась: \(message)", level: "ERROR")
                }
            }
        }
    }

    private func appendInstallLog(_ text: String) {
        installLog += text
        if installLog.count > 12_000 {
            installLog = String(installLog.suffix(12_000))
        }
        let line = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !line.isEmpty {
            statusDetail = String(line.suffix(180))
            note("Установка: \(line)")
        }
    }

    // MARK: - Dictation

    func hotkeyPressed() {
        if case .dictating(true) = phase {
            suppressRelease = true
            stopDictationAndTranscribe()
            return
        }
        if case .dictating(false) = phase {
            return
        }
        guard phase == .idle else {
            let message = phase == .recordingCall || phase == .transcribingCall
                ? "Сначала остановите запись звонка"
                : "Подождите, идёт распознавание"
            note("Хоткей проигнорирован: \(message)", level: "WARN")
            flash(message)
            return
        }
        guard modelStatus.isReady else {
            note("Хоткей проигнорирован: \(modelStatus.menuTitle)", level: "WARN")
            flash(modelStatus.menuTitle)
            if case .missingRuntime = modelStatus {
                onNeedOnboarding?()
            }
            return
        }
        guard permissions.microphone else {
            flash("Нет доступа к микрофону")
            onNeedOnboarding?()
            return
        }
        armWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            self?.startDictation(handsFree: false)
        }
        armWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
    }

    func hotkeyReleased() {
        if suppressRelease {
            suppressRelease = false
            return
        }
        let pending = armWork != nil
        armWork?.cancel()
        armWork = nil
        if pending, case .idle = phase {
            return
        }
        if case .dictating(false) = phase {
            stopDictationAndTranscribe()
        }
    }

    func toggleHandsFree() {
        if case .dictating = phase {
            stopDictationAndTranscribe()
            return
        }
        guard phase == .idle else {
            flash("Сначала завершите текущую запись")
            return
        }
        guard modelStatus.isReady else {
            flash(modelStatus.menuTitle)
            return
        }
        guard permissions.microphone else {
            flash("Нет доступа к микрофону")
            onNeedOnboarding?()
            return
        }
        startDictation(handsFree: true)
    }

    func cancelDictation() {
        armWork?.cancel()
        armWork = nil
        suppressRelease = false
        if case .dictating = phase, let url = microphone.stop() {
            try? FileManager.default.removeItem(at: url)
        }
        if phase != .recordingCall && phase != .transcribingCall {
            phase = .idle
            overlay.hide()
        }
    }

    func copyLastTranscript() {
        guard !lastTranscript.isEmpty else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(lastTranscript, forType: .string)
    }

    func insertHistory(_ entry: DictationEntry) {
        lastTranscript = entry.text
        switch TextInserter.insert(entry.text) {
        case .inserted:
            lastError = nil
        case .secureSkipped:
            lastError = "Поле ввода защищено. Фраза есть в меню."
            flash("Поле защищено — текст в меню")
        case .failed:
            lastError = "Не удалось вставить текст. Он сохранён в меню."
            flash("Не удалось вставить текст")
        }
    }

    // MARK: - Calls

    func toggleCall() {
        if case .recordingCall = phase {
            stopCallAndTranscribe()
            return
        }
        guard phase == .idle else {
            flash("Сначала завершите диктовку")
            return
        }
        guard modelStatus.isReady else {
            flash(modelStatus.menuTitle)
            return
        }
        Task {
            let granted = await Permissions.requestMicrophone()
            DispatchQueue.main.async {
                self.refreshPermissions()
                guard granted else {
                    self.flash("Нет доступа к микрофону")
                    self.onNeedOnboarding?()
                    return
                }
                self.beginCallRecording()
            }
        }
    }

    func deleteCall(_ record: CallRecord) {
        CallStore.delete(record)
        calls = CallStore.load()
    }

    func retranscribeLastCapture() {
        guard let url = lastCaptureURL else {
            note("Нет сохранённой записи для повторного распознавания.", level: "WARN")
            return
        }
        transcribeDebugFile(url)
    }

    func transcribeDebugFile(_ url: URL) {
        guard phase == .idle else {
            flash("Дождитесь окончания текущей операции")
            return
        }
        guard modelStatus.isReady else {
            flash(modelStatus.menuTitle)
            return
        }
        let bytes = Self.fileSize(url)
        lastCaptureURL = url
        lastCaptureBytes = bytes
        phase = .transcribing
        overlay.showTranscribing()
        note("Ручное распознавание. bytes=\(bytes) file=\(url.path)")
        let generation = workerGeneration
        Task {
            do {
                let response = try await self.worker.request(["cmd": "transcribe", "path": url.path])
                let text = (response["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.phase = .idle
                    self.overlay.hide()
                    self.debugResult = text
                    if text.isEmpty {
                        self.lastError = "Речь не распознана"
                        self.note("Ручное распознавание вернуло пустой текст. file=\(url.path)", level: "WARN")
                    } else {
                        self.lastError = nil
                        self.note("Ручное распознавание. chars=\(text.count) text=\(text.prefix(300))")
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.phase = .idle
                    self.overlay.hide()
                    self.lastError = error.localizedDescription
                    self.note("Ошибка ручного распознавания: \(error.localizedDescription)", level: "ERROR")
                }
            }
        }
    }

    func insertDebugResult() {
        let text = debugResult.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        remember(text)
        switch TextInserter.insert(text) {
        case .inserted:
            note("Результат отладки вставлен в активное поле.")
        case .secureSkipped:
            note("Вставка результата отладки пропущена: защищённое поле.", level: "WARN")
        case .failed:
            note("Не удалось вставить результат отладки.", level: "ERROR")
        }
    }

    func retranscribe(_ record: CallRecord) {
        guard phase == .idle else {
            flash("Дождитесь окончания текущей операции")
            return
        }
        guard modelStatus.isReady else {
            flash(modelStatus.menuTitle)
            return
        }
        phase = .transcribingCall
        overlay.showTranscribing()
        if record.isMeeting {
            transcribeMeeting(record)
        } else {
            transcribeCall(record)
        }
    }

    /// Импорт WAV/m4a: одна дорожка → diarization → Собеседник1/2/…
    func importMeetingRecording() {
        guard phase == .idle else {
            flash("Дождитесь окончания текущей операции")
            return
        }
        guard modelStatus.isReady else {
            flash(modelStatus.menuTitle)
            return
        }
        revealForUI()
        let panel = NSOpenPanel()
        panel.title = "Открыть запись встречи"
        panel.message = "Выберите WAV или m4a с диктофона"
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        var types: [UTType] = [.wav]
        if let m4a = UTType(filenameExtension: "m4a") {
            types.append(m4a)
        }
        if let mpeg4Audio = UTType("public.mpeg-4-audio") {
            types.append(mpeg4Audio)
        }
        panel.allowedContentTypes = types
        guard panel.runModal() == .OK, let url = panel.url else { return }
        beginMeetingImport(url)
    }

    private func beginMeetingImport(_ url: URL) {
        let access = url.startAccessingSecurityScopedResource()
        defer {
            if access { url.stopAccessingSecurityScopedResource() }
        }
        let name = url.lastPathComponent
        let record = CallRecord(
            id: UUID(),
            createdAt: Date(),
            duration: 0,
            hasSystemAudio: false,
            segments: [],
            kind: "meeting",
            sourceFileName: name
        )
        do {
            try FileManager.default.createDirectory(at: record.directory, withIntermediateDirectories: true)
            let staged = record.directory.appendingPathComponent("source\(url.pathExtension.isEmpty ? "" : ".\(url.pathExtension)")")
            if FileManager.default.fileExists(atPath: staged.path) {
                try FileManager.default.removeItem(at: staged)
            }
            try FileManager.default.copyItem(at: url, to: staged)
            CallStore.save(record)
            calls = CallStore.load()
            phase = .transcribingCall
            overlay.showTranscribing()
            note("Импорт встречи. file=\(url.path) staged=\(staged.path)")
            let generation = workerGeneration
            let sourcePath = staged.path
            let outWav = record.micURL.path
            Task {
                do {
                    let response = try await self.worker.request([
                        "cmd": "transcribe_file",
                        "path": sourcePath,
                        "out_wav": outWav
                    ])
                    let segments = Self.parseSegments(response["segments"])
                    let duration = (response["duration"] as? NSNumber)?.doubleValue ?? 0
                    DispatchQueue.main.async {
                        guard generation == self.workerGeneration else { return }
                        self.finishMeeting(record.id, segments: segments, duration: duration)
                    }
                } catch {
                    DispatchQueue.main.async {
                        guard generation == self.workerGeneration else { return }
                        self.phase = .idle
                        self.overlay.hide()
                        self.lastError = error.localizedDescription
                        self.flash("Не удалось расшифровать запись")
                        self.note("Ошибка импорта встречи: \(error.localizedDescription)", level: "ERROR")
                        self.calls = CallStore.load()
                        self.onOpenCalls?()
                    }
                }
            }
        } catch {
            CallStore.delete(record)
            lastError = error.localizedDescription
            flash("Не удалось открыть файл")
            note("Ошибка копирования записи встречи: \(error.localizedDescription)", level: "ERROR")
        }
    }

    private func finishMeeting(_ id: UUID, segments: [TranscriptSegment], duration: TimeInterval) {
        phase = .idle
        overlay.hide()
        if var record = CallStore.load().first(where: { $0.id == id }) {
            record.segments = segments
            if duration > 0 {
                record.duration = duration
            } else if FileManager.default.fileExists(atPath: record.micURL.path) {
                record.duration = Self.wavDuration(record.micURL) ?? record.duration
            }
            CallStore.save(record)
        }
        calls = CallStore.load()
        if segments.isEmpty {
            flash("В записи не найдена речь")
        } else {
            lastError = nil
        }
        onOpenCalls?()
    }

    private func transcribeMeeting(_ record: CallRecord) {
        let generation = workerGeneration
        var path = record.micURL.path
        if !FileManager.default.fileExists(atPath: path) {
            let sources = (try? FileManager.default.contentsOfDirectory(at: record.directory, includingPropertiesForKeys: nil)) ?? []
            path = sources.first { $0.lastPathComponent.hasPrefix("source.") }?.path ?? path
        }
        guard FileManager.default.fileExists(atPath: path) else {
            phase = .idle
            overlay.hide()
            lastError = "Файл записи встречи не найден"
            flash(lastError!)
            return
        }
        Task {
            do {
                var payload: [String: Any] = ["cmd": "transcribe_file", "path": path]
                if path != record.micURL.path {
                    payload["out_wav"] = record.micURL.path
                }
                let response = try await self.worker.request(payload)
                let segments = Self.parseSegments(response["segments"])
                let duration = (response["duration"] as? NSNumber)?.doubleValue ?? 0
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.finishMeeting(record.id, segments: segments, duration: duration)
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.phase = .idle
                    self.overlay.hide()
                    self.lastError = error.localizedDescription
                    self.flash("Не удалось расшифровать запись")
                    self.calls = CallStore.load()
                    self.onOpenCalls?()
                }
            }
        }
    }

    private static func wavDuration(_ url: URL) -> TimeInterval? {
        guard let file = try? AVAudioFile(forReading: url) else { return nil }
        let rate = file.processingFormat.sampleRate
        guard rate > 0 else { return nil }
        return Double(file.length) / rate
    }

    // MARK: - Private

    private func startDictation(handsFree: Bool) {
        guard phase == .idle, modelStatus.isReady else { return }
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("gigatts-\(UUID().uuidString).wav")
        do {
            try microphone.start(url: url, deviceUID: selectedDeviceUID)
        } catch {
            lastError = error.localizedDescription
            note("Не удалось начать запись: \(error.localizedDescription)", level: "ERROR")
            flash(error.localizedDescription)
            return
        }
        phase = .dictating(handsFree: handsFree)
        overlay.showDictation()
        note("Запись начата. handsFree=\(handsFree) device=\(selectedDeviceUID.isEmpty ? "system" : selectedDeviceUID) file=\(url.path)")
    }

    private func stopDictationAndTranscribe() {
        armWork?.cancel()
        armWork = nil
        guard case .dictating = phase else { return }
        let duration = microphone.duration
        guard let url = microphone.stop() else {
            phase = .idle
            overlay.hide()
            note("Запись остановилась без файла.", level: "WARN")
            return
        }
        let bytes = Self.fileSize(url)
        note(String(format: "Запись остановлена. duration_s=%.3f bytes=%lld file=%@", duration, bytes, url.path))
        if duration < 0.25 {
            try? FileManager.default.removeItem(at: url)
            phase = .idle
            overlay.hide()
            note("Запись короче 0,25 с, распознавание пропущено.", level: "WARN")
            return
        }
        phase = .transcribing
        overlay.showTranscribing()
        let generation = workerGeneration
        note("Отправка на распознавание. file=\(url.path)")
        Task {
            do {
                let response = try await self.worker.request(["cmd": "transcribe", "path": url.path])
                let text = (response["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.keepLastCapture(url, duration: duration)
                    self.finishDictation(text)
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.keepLastCapture(url, duration: duration)
                    self.phase = .idle
                    self.overlay.hide()
                    self.lastError = error.localizedDescription
                    self.note("Ошибка распознавания: \(error.localizedDescription)", level: "ERROR")
                    self.flash(error.localizedDescription)
                    self.showDebug()
                }
            }
        }
    }

    private func finishDictation(_ text: String) {
        phase = .idle
        overlay.hide()
        debugResult = text
        guard !text.isEmpty else {
            lastError = "Речь не распознана"
            note("Речь не распознана. Модель вернула пустой текст. duration_s=\(String(format: "%.3f", lastCaptureDuration)) bytes=\(lastCaptureBytes) file=\(lastCaptureURL?.path ?? "-")", level: "WARN")
            flash("Речь не распознана")
            showDebug()
            return
        }
        note("Распознано. chars=\(text.count) text=\(text.prefix(300))")
        remember(text)
        switch TextInserter.insert(text) {
        case .inserted:
            lastError = nil
            note("Текст вставлен в активное поле.")
        case .secureSkipped:
            lastError = "Поле ввода защищено. Фраза сохранена в меню."
            note("Вставка пропущена: защищённое поле.", level: "WARN")
            flash("Поле защищено — текст в меню")
        case .failed:
            lastError = "Не удалось вставить текст. Он сохранён в меню."
            note("Не удалось вставить текст.", level: "ERROR")
            flash("Не удалось вставить текст")
        }
    }

    private func remember(_ text: String) {
        lastTranscript = text
        dictationHistory.insert(DictationEntry(id: UUID(), date: Date(), text: text), at: 0)
        if dictationHistory.count > 40 {
            dictationHistory = Array(dictationHistory.prefix(40))
        }
        HistoryStore.save(dictationHistory)
    }

    private func beginCallRecording() {
        guard phase == .idle else { return }
        if !permissions.screenRecording {
            Permissions.requestScreenRecording()
        }
        let record = CallRecord(
            id: UUID(),
            createdAt: Date(),
            duration: 0,
            hasSystemAudio: false,
            segments: []
        )
        CallStore.save(record)
        do {
            try microphone.start(url: record.micURL, deviceUID: selectedDeviceUID)
        } catch {
            lastError = error.localizedDescription
            flash(error.localizedDescription)
            CallStore.delete(record)
            return
        }
        activeCall = record
        phase = .recordingCall
        callElapsed = 0
        overlay.showCall(elapsed: Format.minutes(0))
        callTimer?.invalidate()
        callTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self, case .recordingCall = self.phase else { return }
            self.callElapsed = Date().timeIntervalSince(record.createdAt)
            self.overlay.showCall(elapsed: Format.minutes(self.callElapsed))
        }
        Task {
            do {
                try await self.systemAudio.start(url: record.systemURL) { message in
                    DispatchQueue.main.async {
                        self.lastError = "Системный звук: \(message)"
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    self.lastError = "Системный звук недоступен: \(error.localizedDescription). Пишется только микрофон."
                    self.refreshPermissions()
                }
            }
        }
    }

    private func stopCallAndTranscribe() {
        guard case .recordingCall = phase, var record = activeCall else { return }
        callTimer?.invalidate()
        callTimer = nil
        let micDuration = microphone.duration
        let systemDuration = systemAudio.duration
        _ = microphone.stop()
        _ = systemAudio.stop()
        record.duration = max(micDuration, systemDuration, callElapsed)
        record.hasSystemAudio = systemDuration > 0.3
        CallStore.save(record)
        activeCall = record
        calls = CallStore.load()
        phase = .transcribingCall
        overlay.showTranscribing()
        transcribeCall(record)
    }

    private func transcribeCall(_ record: CallRecord) {
        let generation = workerGeneration
        Task {
            do {
                var payload: [String: Any] = ["cmd": "transcribe_call", "mic": record.micURL.path]
                if FileManager.default.fileExists(atPath: record.systemURL.path) {
                    payload["system"] = record.systemURL.path
                }
                let response = try await self.worker.request(payload)
                let segments = Self.parseSegments(response["segments"])
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.finishCall(record.id, segments: segments)
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.phase = .idle
                    self.overlay.hide()
                    self.lastError = error.localizedDescription
                    self.flash("Не удалось расшифровать звонок")
                    self.calls = CallStore.load()
                    self.onOpenCalls?()
                }
            }
        }
    }

    private func finishCall(_ id: UUID, segments: [TranscriptSegment]) {
        phase = .idle
        overlay.hide()
        activeCall = nil
        if var record = CallStore.load().first(where: { $0.id == id }) {
            record.segments = segments
            CallStore.save(record)
        }
        calls = CallStore.load()
        if segments.isEmpty {
            flash("В записи не найдена речь")
        }
        onOpenCalls?()
    }

    private func startWorker() {
        workerGeneration += 1
        worker.stop()
        logTail = ""
        WorkerLocator.syncBundledScript()
        guard let located = WorkerLocator.locate() else {
            modelStatus = .missingRuntime("Нажмите «Установить распознавание».")
            statusDetail = "Нажмите «Установить распознавание»."
            note("Распознавание не установлено.", level: "WARN")
            return
        }
        note("Запуск воркера. python=\(located.python.path) script=\(located.script.path)")
        do {
            try worker.start(python: located.python, script: located.script, device: modelDevice)
        } catch {
            modelStatus = .failed(error.localizedDescription)
            lastError = error.localizedDescription
            note("Не удалось запустить воркер: \(error.localizedDescription)", level: "ERROR")
            return
        }
        modelStatus = .loading
        statusDetail = "Загрузка v3_e2e_rnnt…"
        let generation = workerGeneration
        Task {
            do {
                _ = try await self.worker.request(["cmd": "load", "model": "v3_e2e_rnnt"])
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.modelStatus = .ready
                    self.statusDetail = self.modelDevice == "mps" ? "Apple GPU" : "CPU"
                    self.lastError = nil
                    self.note("Модель готова. device=\(self.statusDetail)")
                }
            } catch {
                DispatchQueue.main.async {
                    guard generation == self.workerGeneration else { return }
                    self.modelStatus = .failed(error.localizedDescription)
                    self.lastError = error.localizedDescription
                    self.statusDetail = String(self.logTail.suffix(280))
                    self.note("Модель не загрузилась: \(error.localizedDescription) \(self.statusDetail)", level: "ERROR")
                }
            }
        }
    }

    private func absorbLog(_ text: String) {
        logTail += text
        if logTail.count > 4000 {
            logTail = String(logTail.suffix(4000))
        }
        guard case .loading = modelStatus else { return }
        let lowered = text.lowercased()
        if lowered.contains("download") || text.contains("iB") || text.contains("%") {
            statusDetail = "Скачивание весов GigaAM…"
        }
    }

    private func flash(_ text: String) {
        overlay.showMessage(text)
    }

    private func showDebug() {
        mainSection = .debug
        onOpenCalls?()
    }

    private func keepLastCapture(_ url: URL, duration: TimeInterval) {
        let directory = AppPaths.support.appendingPathComponent("debug", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent("last-dictation.wav")
        try? FileManager.default.removeItem(at: destination)
        do {
            try FileManager.default.copyItem(at: url, to: destination)
            lastCaptureURL = destination
            if url.path != destination.path {
                try? FileManager.default.removeItem(at: url)
            }
        } catch {
            lastCaptureURL = url
            note("Не удалось сохранить копию записи: \(error.localizedDescription)", level: "WARN")
        }
        lastCaptureDuration = duration
        lastCaptureBytes = Self.fileSize(lastCaptureURL ?? url)
    }

    private static func fileSize(_ url: URL) -> Int64 {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
    }

    private static func parseSegments(_ value: Any?) -> [TranscriptSegment] {
        guard let rows = value as? [[String: Any]] else { return [] }
        return rows.compactMap { row in
            guard let text = row["text"] as? String else { return nil }
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { return nil }
            let start = (row["start"] as? NSNumber)?.doubleValue ?? 0
            let end = (row["end"] as? NSNumber)?.doubleValue ?? start
            let speaker = row["speaker"] as? String ?? "me"
            return TranscriptSegment(start: start, end: end, speaker: speaker, text: trimmed)
        }
    }
}
