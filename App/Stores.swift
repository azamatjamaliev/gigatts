import Foundation

enum HistoryStore {
    static func load() -> [DictationEntry] {
        guard let data = try? Data(contentsOf: AppPaths.historyFile) else { return [] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([DictationEntry].self, from: data)) ?? []
    }

    static func save(_ entries: [DictationEntry]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(entries) else { return }
        try? data.write(to: AppPaths.historyFile, options: .atomic)
    }
}

enum CallStore {
    static var root: URL {
        let url = AppPaths.support.appendingPathComponent("calls", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func load() -> [CallRecord] {
        let directories = (try? FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        )) ?? []
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let records: [CallRecord] = directories.compactMap { directory in
            let meta = directory.appendingPathComponent("meta.json")
            guard let data = try? Data(contentsOf: meta) else { return nil }
            return try? decoder.decode(CallRecord.self, from: data)
        }
        return records.sorted { $0.createdAt > $1.createdAt }
    }

    static func save(_ record: CallRecord) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(record) else { return }
        try? FileManager.default.createDirectory(at: record.directory, withIntermediateDirectories: true)
        try? data.write(to: record.directory.appendingPathComponent("meta.json"), options: .atomic)
    }

    static func delete(_ record: CallRecord) {
        try? FileManager.default.removeItem(at: record.directory)
    }
}
