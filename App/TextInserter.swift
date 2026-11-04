import AppKit
import ApplicationServices
import Carbon
import CoreGraphics

enum TextInserter {
    enum Outcome {
        case inserted
        case secureSkipped
        case failed
    }

    static func insert(_ text: String) -> Outcome {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .failed }
        if IsSecureEventInputEnabled() {
            return .secureSkipped
        }
        switch insertViaAccessibility(trimmed) {
        case .inserted:
            return .inserted
        case .secureSkipped:
            return .secureSkipped
        case .failed:
            return insertViaPasteboard(trimmed) ? .inserted : .failed
        }
    }

    private static func insertViaAccessibility(_ text: String) -> Outcome {
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        let copyResult = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused)
        guard copyResult == .success, let focused else { return .failed }
        guard CFGetTypeID(focused) == AXUIElementGetTypeID() else { return .failed }
        let element = focused as! AXUIElement

        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
        if let role = role as? String, role == "AXSecureTextField" {
            return .secureSkipped
        }

        let before = stringAttribute(element, kAXValueAttribute as CFString)
        let range = selectedRange(element)
        let setResult = AXUIElementSetAttributeValue(element, kAXSelectedTextAttribute as CFString, text as CFTypeRef)
        if setResult == .success {
            AppLog.shared.write("INFO", "Вставка через Accessibility.")
            return .inserted
        }
        if landed(text, before: before, range: range, element: element)
            || (before != nil && waitAndLanded(text, before: before, range: range, element: element)) {
            AppLog.shared.write("INFO", "Текст уже в поле после Accessibility (код \(setResult.rawValue)), ⌘V пропущено.")
            return .inserted
        }
        AppLog.shared.write("INFO", "Accessibility не изменила поле (код \(setResult.rawValue)).")
        return .failed
    }

    private static func waitAndLanded(_ text: String, before: String?, range: NSRange?, element: AXUIElement) -> Bool {
        Thread.sleep(forTimeInterval: 0.05)
        return landed(text, before: before, range: range, element: element)
    }

    private static func landed(_ text: String, before: String?, range: NSRange?, element: AXUIElement) -> Bool {
        guard let before,
              let after = stringAttribute(element, kAXValueAttribute as CFString),
              after != before else {
            return false
        }
        if let range, let expected = replacing(before, range: range, with: text), after == expected {
            return true
        }
        let added = (text as NSString).length
        guard added > 0, (after as NSString).length == (before as NSString).length + added else { return false }
        return occurrenceCount(of: text, in: after) == occurrenceCount(of: text, in: before) + 1
    }

    private static func replacing(_ value: String, range: NSRange, with text: String) -> String? {
        let ns = value as NSString
        let end = NSMaxRange(range)
        guard range.location != NSNotFound, range.location >= 0, range.length >= 0, end <= ns.length else { return nil }
        return ns.replacingCharacters(in: range, with: text)
    }

    private static func occurrenceCount(of needle: String, in haystack: String) -> Int {
        guard !needle.isEmpty else { return 0 }
        var count = 0
        var search = haystack.startIndex
        while let found = haystack.range(of: needle, range: search..<haystack.endIndex) {
            count += 1
            search = found.upperBound
        }
        return count
    }

    private static func stringAttribute(_ element: AXUIElement, _ attribute: CFString) -> String? {
        var raw: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &raw) == .success, let raw else { return nil }
        return raw as? String
    }

    private static func selectedRange(_ element: AXUIElement) -> NSRange? {
        var raw: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &raw) == .success,
              let raw,
              CFGetTypeID(raw) == AXValueGetTypeID() else {
            return nil
        }
        var range = CFRange(location: 0, length: 0)
        guard AXValueGetValue(raw as! AXValue, .cfRange, &range),
              range.location >= 0,
              range.length >= 0 else {
            return nil
        }
        return NSRange(location: range.location, length: range.length)
    }

    private static func insertViaPasteboard(_ text: String) -> Bool {
        let pasteboard = NSPasteboard.general
        let snapshot = PasteboardSnapshot.capture(pasteboard)
        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else { return false }
        let marker = pasteboard.changeCount
        AppLog.shared.write("INFO", "Вставка через ⌘V.")
        postCommandV()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) {
            if NSPasteboard.general.changeCount == marker {
                snapshot.restore(to: NSPasteboard.general)
            }
        }
        return true
    }

    private static func postCommandV() {
        let source = CGEventSource(stateID: .combinedSessionState)
        let keyCode: CGKeyCode = 0x09
        guard let down = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false) else {
            return
        }
        down.flags = .maskCommand
        up.flags = .maskCommand
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
}

private struct PasteboardSnapshot {
    struct Piece {
        var type: NSPasteboard.PasteboardType
        var data: Data
    }

    var items: [[Piece]]

    static func capture(_ pasteboard: NSPasteboard) -> PasteboardSnapshot {
        let items = (pasteboard.pasteboardItems ?? []).map { item in
            item.types.compactMap { type -> Piece? in
                guard let data = item.data(forType: type) else { return nil }
                return Piece(type: type, data: data)
            }
        }
        return PasteboardSnapshot(items: items)
    }

    func restore(to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        let objects = items.map { pieces -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for piece in pieces {
                item.setData(piece.data, forType: piece.type)
            }
            return item
        }
        if !objects.isEmpty {
            pasteboard.writeObjects(objects)
        }
    }
}
