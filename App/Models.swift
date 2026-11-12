import AppKit
import ApplicationServices
import CoreAudio
import SwiftUI

struct Hotkey: Equatable {
    var keyCode: UInt16
    var modifiers: UInt64
    var isModifier: Bool

    static let rightOption = Hotkey(keyCode: 0x3D, modifiers: 0, isModifier: true)

    var modifierFlags: CGEventFlags {
        CGEventFlags(rawValue: modifiers)
    }

    var displayName: String {
        if isModifier, let name = Hotkey.modifierName(keyCode) {
            return name
        }
        var parts: [String] = []
        let flags = modifierFlags
        if flags.contains(.maskControl) { parts.append("⌃") }
        if flags.contains(.maskAlternate) { parts.append("⌥") }
        if flags.contains(.maskShift) { parts.append("⇧") }
        if flags.contains(.maskCommand) { parts.append("⌘") }
        parts.append(Hotkey.keyName(keyCode))
        return parts.joined()
    }

    static func modifierName(_ keyCode: UInt16) -> String? {
        switch keyCode {
        case 0x3A: return "Левый Option"
        case 0x3D: return "Правый Option"
        case 0x38: return "Левый Shift"
        case 0x3C: return "Правый Shift"
        case 0x3B: return "Левый Control"
        case 0x3E: return "Правый Control"
        case 0x37: return "Левый Command"
        case 0x36: return "Правый Command"
        case 0x3F: return "Fn"
        default: return nil
        }
    }

    static func isModifierKeyCode(_ keyCode: UInt16) -> Bool {
        modifierName(keyCode) != nil
    }

    static func isModifierDown(keyCode: UInt16, flags: CGEventFlags) -> Bool {
        switch keyCode {
        case 0x3A, 0x3D: return flags.contains(.maskAlternate)
        case 0x38, 0x3C: return flags.contains(.maskShift)
        case 0x3B, 0x3E: return flags.contains(.maskControl)
        case 0x37, 0x36: return flags.contains(.maskCommand)
        case 0x3F: return flags.contains(.maskSecondaryFn)
        default: return false
        }
    }

    static func keyName(_ keyCode: UInt16) -> String {
        if let name = modifierName(keyCode) { return name }
        let names: [UInt16: String] = [
            0x00: "A", 0x01: "S", 0x02: "D", 0x03: "F", 0x04: "H", 0x05: "G", 0x06: "Z", 0x07: "X",
            0x08: "C", 0x09: "V", 0x0B: "B", 0x0C: "Q", 0x0D: "W", 0x0E: "E", 0x0F: "R",
            0x10: "Y", 0x11: "T", 0x12: "1", 0x13: "2", 0x14: "3", 0x15: "4", 0x16: "6", 0x17: "5",
            0x18: "=", 0x19: "9", 0x1A: "7", 0x1B: "-", 0x1C: "8", 0x1D: "0", 0x1E: "]", 0x1F: "O",
            0x20: "U", 0x21: "[", 0x22: "I", 0x23: "P", 0x25: "L", 0x26: "J", 0x27: "'", 0x28: "K",
            0x29: ";", 0x2A: "\\", 0x2B: ",", 0x2C: "/", 0x2D: "N", 0x2E: "M", 0x2F: ".",
            0x31: "Space", 0x24: "Return", 0x30: "Tab", 0x33: "Delete", 0x35: "Esc",
            0x7B: "←", 0x7C: "→", 0x7D: "↓", 0x7E: "↑"
        ]
        return names[keyCode] ?? "Клавиша \(keyCode)"
    }

    static func interestingModifiers(_ flags: CGEventFlags) -> CGEventFlags {
        flags.intersection([.maskCommand, .maskShift, .maskAlternate, .maskControl])
    }
}

enum SessionPhase: Equatable {
    case idle
    case dictating(handsFree: Bool)
    case transcribing
    case recordingCall
    case transcribingCall
}

enum MainSection: String, CaseIterable, Identifiable {
    case calls
    case debug

    var id: String { rawValue }

    var title: String {
        switch self {
        case .calls: return "Звонки"
        case .debug: return "Отладка"
        }
    }
}

enum ModelStatus: Equatable {
    case starting
    case missingRuntime(String)
    case loading
    case ready
    case failed(String)

    var isReady: Bool {
        if case .ready = self { return true }
        return false
    }

    var menuTitle: String {
        switch self {
        case .starting: return "Запуск распознавания…"
        case .missingRuntime: return "Модель не установлена"
        case .loading: return "Загрузка модели…"
        case .ready: return "GigaAM v3 готова"
        case .failed: return "Ошибка модели"
        }
    }
}

struct DictationEntry: Codable, Identifiable, Hashable {
    var id: UUID
    var date: Date
    var text: String

    var preview: String {
        let flat = text.replacingOccurrences(of: "\n", with: " ")
        if flat.count <= 72 { return flat }
        return String(flat.prefix(72)) + "…"
    }
}

struct TranscriptSegment: Codable, Hashable, Identifiable {
    var id: UUID
    var start: Double
    var end: Double
    var speaker: String
    var text: String

    var speakerTitle: String {
        if speaker == "me" { return "Я" }
        if speaker == "them" { return "Собеседник" }
        if speaker.hasPrefix("them"), let number = Int(speaker.dropFirst(4)), number > 0 {
            return "Собеседник\(number)"
        }
        return "Собеседник"
    }

    init(id: UUID = UUID(), start: Double, end: Double, speaker: String, text: String) {
        self.id = id
        self.start = start
        self.end = end
        self.speaker = speaker
        self.text = text
    }

    enum CodingKeys: String, CodingKey {
        case id, start, end, speaker, text
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        start = try container.decode(Double.self, forKey: .start)
        end = try container.decode(Double.self, forKey: .end)
        speaker = try container.decode(String.self, forKey: .speaker)
        text = try container.decode(String.self, forKey: .text)
    }
}

struct CallRecord: Codable, Identifiable, Hashable {
    var id: UUID
    var createdAt: Date
    var duration: TimeInterval
    var hasSystemAudio: Bool
    var segments: [TranscriptSegment]
    /// "call" — запись из меню; "meeting" — импорт WAV/m4a.
    var kind: String
    var sourceFileName: String?

    var isMeeting: Bool { kind == "meeting" }

    var title: String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "ru_RU")
        formatter.dateFormat = "d MMM, HH:mm"
        let when = formatter.string(from: createdAt)
        if isMeeting {
            if let name = sourceFileName, !name.isEmpty {
                return "Встреча · \(name)"
            }
            return "Встреча \(when)"
        }
        return "Звонок \(when)"
    }

    var directory: URL {
        CallStore.root.appendingPathComponent(id.uuidString, isDirectory: true)
    }

    var micURL: URL { directory.appendingPathComponent("mic.wav") }
    var systemURL: URL { directory.appendingPathComponent("system.wav") }

    var plainTranscript: String {
        if segments.isEmpty { return "Расшифровки пока нет." }
        return segments.map { segment in
            "[\(Format.clock(segment.start))] \(segment.speakerTitle): \(segment.text)"
        }.joined(separator: "\n")
    }

    init(
        id: UUID = UUID(),
        createdAt: Date = Date(),
        duration: TimeInterval = 0,
        hasSystemAudio: Bool = false,
        segments: [TranscriptSegment] = [],
        kind: String = "call",
        sourceFileName: String? = nil
    ) {
        self.id = id
        self.createdAt = createdAt
        self.duration = duration
        self.hasSystemAudio = hasSystemAudio
        self.segments = segments
        self.kind = kind
        self.sourceFileName = sourceFileName
    }

    enum CodingKeys: String, CodingKey {
        case id, createdAt, duration, hasSystemAudio, segments, kind, sourceFileName
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        createdAt = try container.decode(Date.self, forKey: .createdAt)
        duration = try container.decode(Double.self, forKey: .duration)
        hasSystemAudio = try container.decode(Bool.self, forKey: .hasSystemAudio)
        segments = try container.decodeIfPresent([TranscriptSegment].self, forKey: .segments) ?? []
        kind = try container.decodeIfPresent(String.self, forKey: .kind) ?? "call"
        sourceFileName = try container.decodeIfPresent(String.self, forKey: .sourceFileName)
    }
}

enum Format {
    static func clock(_ interval: TimeInterval) -> String {
        let tenths = Int((interval * 10).rounded())
        let minutes = tenths / 600
        let seconds = (tenths / 10) % 60
        let fraction = tenths % 10
        return String(format: "%02d:%02d.%d", minutes, seconds, fraction)
    }

    static func minutes(_ interval: TimeInterval) -> String {
        let total = max(0, Int(interval.rounded(.down)))
        return String(format: "%02d:%02d", total / 60, total % 60)
    }
}

struct PermissionSnapshot: Equatable {
    var microphone = false
    var inputMonitoring = false
    var accessibility = false
    var screenRecording = false

    var readyForDictation: Bool {
        microphone && inputMonitoring && accessibility
    }
}

struct AudioInputDevice: Identifiable, Hashable {
    var id: String { uid }
    var uid: String
    var name: String
    var deviceID: AudioDeviceID
}

enum DefaultsKey {
    static let keyCode = "hotkey.keyCode"
    static let modifiers = "hotkey.modifiers"
    static let isModifier = "hotkey.isModifier"
    static let deviceUID = "audio.deviceUID"
    static let onboarding = "didFinishOnboarding"
    static let modelDevice = "model.device"
}
