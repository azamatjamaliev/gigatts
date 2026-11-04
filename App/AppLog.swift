import Foundation

final class AppLog: ObservableObject {
    static let shared = AppLog()

    @Published private(set) var text = ""
    let fileURL: URL
    private let queue = DispatchQueue(label: "gigatts.log")
    private let formatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private init() {
        let directory = AppPaths.support.appendingPathComponent("logs", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("gigatts.log")
        if let existing = try? String(contentsOf: fileURL, encoding: .utf8) {
            text = String(existing.suffix(60_000))
        }
    }

    func write(_ level: String, _ message: String) {
        let clean = message
            .replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " | ")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return }
        let line = "\(formatter.string(from: Date())) \(level) \(clean)\n"
        let url = fileURL
        queue.async {
            self.append(line, to: url)
        }
        DispatchQueue.main.async {
            self.text += line
            if self.text.count > 80_000 {
                self.text = String(self.text.suffix(80_000))
            }
        }
    }

    func clear() {
        let url = fileURL
        queue.async {
            try? Data().write(to: url)
        }
        DispatchQueue.main.async {
            self.text = ""
        }
        write("INFO", "Журнал очищен.")
    }

    private func append(_ line: String, to url: URL) {
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data(line.utf8))
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
        guard size > 4_000_000, let data = try? Data(contentsOf: url) else { return }
        let kept = data.suffix(1_000_000)
        try? kept.write(to: url, options: .atomic)
    }
}
