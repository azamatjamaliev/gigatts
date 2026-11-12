import Foundation

enum WorkerLocator {
    struct Located {
        var python: URL
        var script: URL
    }

    /// Keep Application Support worker.py aligned with the copy inside the .app
    /// (new RPC commands like transcribe_file after an app update).
    static func syncBundledScript() {
        guard let source = bundledWorkerURL() else { return }
        let destination = AppPaths.support.appendingPathComponent("worker.py")
        if source.standardizedFileURL == destination.standardizedFileURL { return }
        do {
            try FileManager.default.createDirectory(at: AppPaths.support, withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            try FileManager.default.copyItem(at: source, to: destination)
        } catch {
            // Keep the previously installed script if refresh fails.
        }
    }

    static func locate() -> Located? {
        let environment = ProcessInfo.processInfo.environment
        if let python = environment["GIGATTS_PYTHON"], let script = environment["GIGATTS_WORKER"] {
            let pythonURL = URL(fileURLWithPath: python)
            let scriptURL = URL(fileURLWithPath: script)
            if FileManager.default.isExecutableFile(atPath: pythonURL.path),
               FileManager.default.fileExists(atPath: scriptURL.path) {
                return Located(python: pythonURL, script: scriptURL)
            }
        }

        let support = AppPaths.support
        let pythonPath = support.appendingPathComponent("python-path")
        let scriptPath = support.appendingPathComponent("worker-path")
        if let python = try? String(contentsOf: pythonPath, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
           let script = try? String(contentsOf: scriptPath, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
           FileManager.default.isExecutableFile(atPath: python),
           FileManager.default.fileExists(atPath: script) {
            return Located(python: URL(fileURLWithPath: python), script: URL(fileURLWithPath: script))
        }

        let supportPython = AppPaths.support.appendingPathComponent("venv/bin/python")
        let supportWorker = AppPaths.support.appendingPathComponent("worker.py")
        if FileManager.default.isExecutableFile(atPath: supportPython.path) {
            if FileManager.default.fileExists(atPath: supportWorker.path) {
                return Located(python: supportPython, script: supportWorker)
            }
            if let bundled = bundledWorkerURL() {
                return Located(python: supportPython, script: bundled)
            }
        }

        var bases: [URL] = [URL(fileURLWithPath: FileManager.default.currentDirectoryPath)]
        if let resource = Bundle.main.resourceURL {
            bases.append(resource)
        }
        var cursor = Bundle.main.bundleURL
        for _ in 0..<7 {
            bases.append(cursor)
            cursor.deleteLastPathComponent()
        }

        for base in bases {
            let directories = [
                base.appendingPathComponent("Worker"),
                base
            ]
            for directory in directories {
                let script = directory.appendingPathComponent("worker.py")
                let python = directory.appendingPathComponent(".venv/bin/python")
                if FileManager.default.isExecutableFile(atPath: python.path),
                   FileManager.default.fileExists(atPath: script.path) {
                    return Located(python: python, script: script)
                }
            }
        }
        return nil
    }

    static let diarizationDirName = "pyannote-speaker-diarization-community-1"

    static func bundledWorkerURL() -> URL? {
        bundledResource("worker.py") ?? developmentFile("Worker/worker.py")
    }

    static func bundledRequirementsURL() -> URL? {
        bundledResource("requirements.txt") ?? developmentFile("Worker/requirements.txt")
    }

    /// Packaged pyannote weights inside the .app (or dist/models / cache while developing).
    static func bundledDiarizationModelURL() -> URL? {
        let relative = "models/\(diarizationDirName)"
        if let resource = Bundle.main.resourceURL {
            let url = resource.appendingPathComponent(relative)
            if diarizationReady(url) { return url }
        }
        if let url = developmentFile("dist/models/\(diarizationDirName)"), diarizationReady(url) {
            return url
        }
        let cache = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".cache/gigatts/\(diarizationDirName)")
        if diarizationReady(cache) { return cache }
        return nil
    }

    static func applyDiarizationEnvironment(_ environment: inout [String: String]) {
        if environment["GIGATTS_DIARIZATION_MODEL"]?.isEmpty == false { return }
        if let url = bundledDiarizationModelURL() {
            environment["GIGATTS_DIARIZATION_MODEL"] = url.path
        }
    }

    static func diarizationReady(_ root: URL) -> Bool {
        let required = [
            "config.yaml",
            "embedding/pytorch_model.bin",
            "segmentation/pytorch_model.bin",
            "plda/plda.npz",
            "plda/xvec_transform.npz"
        ]
        return required.allSatisfy { FileManager.default.fileExists(atPath: root.appendingPathComponent($0).path) }
    }

    /// Resources inside the .app. This stays valid after the app is moved to /Applications.
    private static func bundledResource(_ name: String) -> URL? {
        let candidates = [
            Bundle.main.resourceURL?.appendingPathComponent(name),
            Bundle.main.url(forResource: (name as NSString).deletingPathExtension, withExtension: (name as NSString).pathExtension)
        ]
        for case let url? in candidates where FileManager.default.fileExists(atPath: url.path) {
            return url
        }
        return nil
    }

    /// Repo-relative lookup is only for `swift run`. A packaged app must not depend on the folder it was launched from.
    private static func developmentFile(_ relativePath: String) -> URL? {
        guard Bundle.main.bundleURL.pathExtension != "app" else { return nil }
        return nearestFile(relativePath)
    }

    private static func nearestFile(_ relativePath: String) -> URL? {
        var bases = [URL(fileURLWithPath: FileManager.default.currentDirectoryPath)]
        var cursor = Bundle.main.bundleURL
        for _ in 0..<7 {
            bases.append(cursor)
            cursor.deleteLastPathComponent()
        }
        for base in bases {
            let url = base.appendingPathComponent(relativePath)
            if FileManager.default.fileExists(atPath: url.path) {
                return url
            }
        }
        return nil
    }
}

enum AppPaths {
    static var support: URL {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("GigaTTS", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static var historyFile: URL {
        support.appendingPathComponent("dictation-history.json")
    }
}

enum InstallError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self {
        case .message(let text): return text
        }
    }
}

enum WorkerInstaller {
    static let searchPath = "/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
    private static let searchDirectories = [
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin"
    ]
    private static let requirementsText = """
    torch>=2.6
    torchaudio>=2.6
    gigaam @ git+https://github.com/salute-developers/GigaAM.git
    truststore>=0.9
    pyannote.audio>=3.3
    huggingface_hub>=0.24
    """

    static func install(log: @escaping (String) -> Void) async throws {
        log("Приложение: \(Bundle.main.bundleURL.path)\n")
        log("Данные: \(AppPaths.support.path)\n")
        let workerSource = try workerSource()
        let requirements = try requirementsFile()
        let installedWorker = AppPaths.support.appendingPathComponent("worker.py")
        if installedWorker.path != workerSource.path {
            if FileManager.default.fileExists(atPath: installedWorker.path) {
                try FileManager.default.removeItem(at: installedWorker)
            }
            try FileManager.default.copyItem(at: workerSource, to: installedWorker)
        }

        log("Ищу Python 3.10+ и ffmpeg…\n")
        var python = findPython()
        if python == nil {
            log("Python 3.10+ не найден, ставлю python@3.12 через Homebrew…\n")
            let brew = try requireBrew()
            try await run(brew, ["install", "python@3.12"], log: log)
            python = findPython()
        }
        guard let basePython = python else {
            throw InstallError.message("Не удалось найти Python 3.10 или новее.")
        }
        log("Python: \(basePython.path)\n")

        if findExecutable("ffmpeg") == nil {
            log("ffmpeg не найден, ставлю через Homebrew…\n")
            let brew = try requireBrew()
            try await run(brew, ["install", "ffmpeg"], log: log)
            guard findExecutable("ffmpeg") != nil else {
                throw InstallError.message("ffmpeg не появился после установки.")
            }
        }

        let venvPython = AppPaths.support.appendingPathComponent("venv/bin/python")
        if !FileManager.default.isExecutableFile(atPath: venvPython.path) {
            log("Создаю локальное окружение…\n")
            try await run(basePython, ["-m", "venv", AppPaths.support.appendingPathComponent("venv").path], log: log)
        }
        log("Устанавливаю PyTorch, GigaAM и pyannote. Первый раз это занимает несколько минут…\n")
        try await run(venvPython, ["-u", "-m", "pip", "install", "--upgrade", "pip"], log: log)
        try await run(venvPython, ["-u", "-m", "pip", "install", "-r", requirements.path], log: log)
        if let bundled = WorkerLocator.bundledDiarizationModelURL() {
            log("Использую встроенную модель разделения спикеров: \(bundled.path)\n")
        } else {
            log("Скачиваю модель разделения спикеров (pyannote)…\n")
            do {
                try await run(venvPython, ["-u", installedWorker.path, "--download-diarization"], log: log)
            } catch {
                log(
                    "Разделение спикеров отложено: в сборке нет models/, либо задайте HF_TOKEN "
                        + "(или ~/.cache/gigatts/hf-token). Диктовка и две дорожки звонка работают без этого.\n"
                )
            }
        }

        try (venvPython.path + "\n").write(
            to: AppPaths.support.appendingPathComponent("python-path"),
            atomically: true,
            encoding: .utf8
        )
        try (installedWorker.path + "\n").write(
            to: AppPaths.support.appendingPathComponent("worker-path"),
            atomically: true,
            encoding: .utf8
        )
        log("Файлы распознавания готовы.\n")
    }

    private static func workerSource() throws -> URL {
        if let url = WorkerLocator.bundledWorkerURL() {
            return url
        }
        throw InstallError.message("В приложении нет worker.py. Пересоберите GigaПисец.")
    }

    private static func requirementsFile() throws -> URL {
        let destination = AppPaths.support.appendingPathComponent("requirements.txt")
        if let bundled = WorkerLocator.bundledRequirementsURL() {
            if bundled.path != destination.path {
                if FileManager.default.fileExists(atPath: destination.path) {
                    try FileManager.default.removeItem(at: destination)
                }
                try FileManager.default.copyItem(at: bundled, to: destination)
            }
            return destination
        }
        try requirementsText.write(to: destination, atomically: true, encoding: .utf8)
        return destination
    }

    private static func requireBrew() throws -> URL {
        if let brew = findExecutable("brew") {
            return brew
        }
        throw InstallError.message("Нужен Homebrew, чтобы поставить недостающий Python или ffmpeg: https://brew.sh")
    }

    private static func findPython() -> URL? {
        for name in ["python3.13", "python3.12", "python3.11", "python3.10"] {
            if let url = findExecutable(name) {
                return url
            }
        }
        return nil
    }

    private static func findExecutable(_ name: String) -> URL? {
        for directory in searchDirectories {
            let url = URL(fileURLWithPath: directory).appendingPathComponent(name)
            if FileManager.default.isExecutableFile(atPath: url.path) {
                return url
            }
        }
        return nil
    }

    private static func run(_ executable: URL, _ arguments: [String], log: @escaping (String) -> Void) async throws {
        let command = ([executable.lastPathComponent] + arguments).joined(separator: " ")
        log("\n$ \(command)\n")
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let process = Process()
            let pipe = Pipe()
            process.executableURL = executable
            process.arguments = arguments
            process.currentDirectoryURL = AppPaths.support
            var environment = ProcessInfo.processInfo.environment
            environment["PATH"] = searchPath
            environment["HOME"] = FileManager.default.homeDirectoryForCurrentUser.path
            environment["HOMEBREW_NO_AUTO_UPDATE"] = "1"
            environment["HOMEBREW_NO_ENV_HINTS"] = "1"
            environment["PYTHONUNBUFFERED"] = "1"
            environment["PYTHONIOENCODING"] = "utf-8"
            environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"
            environment["GIT_TERMINAL_PROMPT"] = "0"
            WorkerLocator.applyDiarizationEnvironment(&environment)
            process.environment = environment
            process.standardOutput = pipe
            process.standardError = pipe

            let finishedReading = DispatchSemaphore(value: 0)
            let reader = DispatchQueue(label: "gigatts.install.read")
            let once = ResumeOnce(continuation)
            reader.async {
                let handle = pipe.fileHandleForReading
                while true {
                    let data = handle.availableData
                    if data.isEmpty { break }
                    if let text = String(data: data, encoding: .utf8) {
                        let normalized = normalizeLog(text)
                        if !normalized.isEmpty { log(normalized) }
                    }
                }
                finishedReading.signal()
            }

            process.terminationHandler = { process in
                finishedReading.wait()
                if process.terminationStatus == 0 {
                    once.call(nil)
                } else {
                    once.call(InstallError.message(
                        "\(executable.lastPathComponent) завершился с кодом \(process.terminationStatus)."
                    ))
                }
            }
            do {
                try process.run()
            } catch {
                try? pipe.fileHandleForReading.close()
                once.call(error)
            }
        }
    }

    private static func normalizeLog(_ text: String) -> String {
        text
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
    }
}

private final class ResumeOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var resumed = false
    private let continuation: CheckedContinuation<Void, Error>

    init(_ continuation: CheckedContinuation<Void, Error>) {
        self.continuation = continuation
    }

    func call(_ error: Error?) {
        lock.lock()
        defer { lock.unlock() }
        guard !resumed else { return }
        resumed = true
        if let error {
            continuation.resume(throwing: error)
        } else {
            continuation.resume()
        }
    }
}

final class WorkerClient {
    var onLog: ((String) -> Void)?
    var onExit: ((Int32) -> Void)?

    private var process: Process?
    private var stdinHandle: FileHandle?
    private var stdoutBuffer = Data()
    private var pending: [String: (Result<[String: Any], Error>) -> Void] = [:]
    private let lock = NSLock()
    private var stopped = false

    var isRunning: Bool { process?.isRunning == true }

    func start(python: URL, script: URL, device: String) throws {
        stop()
        stopped = false
        let process = Process()
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        process.executableURL = python
        process.arguments = ["-u", script.path]
        process.currentDirectoryURL = script.deletingLastPathComponent()
        var environment = ProcessInfo.processInfo.environment
        environment["PYTHONUNBUFFERED"] = "1"
        environment["GIGATTS_DEVICE"] = device
        environment["PATH"] = WorkerInstaller.searchPath
        WorkerLocator.applyDiarizationEnvironment(&environment)
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        process.terminationHandler = { [weak self] process in
            self?.failPending("Процесс распознавания завершился (код \(process.terminationStatus)).")
            DispatchQueue.main.async {
                self?.onExit?(process.terminationStatus)
            }
        }
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if !data.isEmpty {
                self?.consume(data)
            }
        }
        errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            DispatchQueue.main.async {
                self?.onLog?(text)
            }
        }
        try process.run()
        self.process = process
        stdinHandle = input.fileHandleForWriting
    }

    func stop() {
        stopped = true
        process?.terminationHandler = nil
        if let stdinHandle {
            try? stdinHandle.close()
        }
        stdinHandle = nil
        if process?.isRunning == true {
            process?.terminate()
        }
        process = nil
        failPending("Распознавание остановлено.")
    }

    func request(_ payload: [String: Any]) async throws -> [String: Any] {
        let id = UUID().uuidString
        var body = payload
        body["id"] = id
        let data = try JSONSerialization.data(withJSONObject: body)
        return try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            pending[id] = { result in
                continuation.resume(with: result)
            }
            lock.unlock()
            do {
                var line = data
                line.append(0x0A)
                guard let stdinHandle else {
                    throw WorkerError.message("Воркер не запущен.")
                }
                try stdinHandle.write(contentsOf: line)
            } catch {
                lock.lock()
                pending.removeValue(forKey: id)
                lock.unlock()
                continuation.resume(throwing: error)
            }
        }
    }

    private func consume(_ data: Data) {
        lock.lock()
        stdoutBuffer.append(data)
        var lines: [Data] = []
        while let newline = stdoutBuffer.firstIndex(of: 0x0A) {
            let line = stdoutBuffer.prefix(upTo: newline)
            stdoutBuffer.removeSubrange(stdoutBuffer.startIndex...newline)
            if !line.isEmpty {
                lines.append(Data(line))
            }
        }
        lock.unlock()
        for line in lines {
            handle(line)
        }
    }

    private func handle(_ line: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { return }
        let id = object["id"] as? String ?? ""
        lock.lock()
        let waiter = pending.removeValue(forKey: id)
        lock.unlock()
        guard let waiter else { return }
        if (object["ok"] as? Bool) == false {
            let message = object["error"] as? String ?? "Ошибка распознавания"
            waiter(.failure(WorkerError.message(message)))
        } else {
            waiter(.success(object))
        }
    }

    private func failPending(_ message: String) {
        lock.lock()
        let waiters = pending
        pending.removeAll()
        lock.unlock()
        if stopped && waiters.isEmpty { return }
        for waiter in waiters.values {
            waiter(.failure(WorkerError.message(message)))
        }
    }
}

enum WorkerError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self {
        case .message(let text): return text
        }
    }
}
