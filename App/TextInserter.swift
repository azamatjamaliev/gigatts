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

    /// Prevents a second insert if finishDictation / UI somehow call insert twice quickly.
    private static var lastInsertAt: CFAbsoluteTime = 0
    private static var lastInsertText: String = ""

    static func insert(_ text: String) -> Outcome {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .failed }
        if IsSecureEventInputEnabled() {
            return .secureSkipped
        }
        let element = focusedElement()
        if let element, isSecureTextField(element) {
            return .secureSkipped
        }

        let now = CFAbsoluteTimeGetCurrent()
        if trimmed == lastInsertText, now - lastInsertAt < 0.85 {
            AppLog.shared.write("INFO", "Повторная вставка пропущена (защита от дубля).")
            return .inserted
        }

        let before = element.flatMap { stringAttribute($0, kAXValueAttribute as CFString) }
        let range = element.flatMap { selectedRange($0) }

        // Single path first: clipboard + one ⌘V.
        // Telegram / Cursor accept ⌘V but AX often lags or also accepts AXSelectedText —
        // trusting paste skips dual pasteLanded polls + AX (avoids Telegram duplicate and
        // Cursor reading the clipboard after a premature restore).
        if insertViaPasteboard(trimmed) {
            if let element, let before {
                if shouldTrustPasteWithoutAccessibility(before: before) {
                    markInserted(trimmed)
                    AppLog.shared.write(
                        "INFO",
                        "Вставка через ⌘V (AX пропущен — иначе дубль / гонка буфера в Telegram, Cursor и похожих полях)."
                    )
                    return .inserted
                }
                if pasteLanded(trimmed, before: before, range: range, element: element) {
                    markInserted(trimmed)
                    AppLog.shared.write("INFO", "Вставка через ⌘V.")
                    return .inserted
                }
                // Last look before AX: paste may have landed after the first poll window.
                if pasteLanded(trimmed, before: before, range: range, element: element) {
                    markInserted(trimmed)
                    AppLog.shared.write("INFO", "Вставка через ⌘V.")
                    return .inserted
                }
                AppLog.shared.write("INFO", "⌘V не изменила поле.")
                let outcome = insertViaAccessibility(trimmed, element: element, before: before, range: range)
                if outcome == .inserted { markInserted(trimmed) }
                return outcome
            }
            markInserted(trimmed)
            AppLog.shared.write("INFO", "Вставка через ⌘V.")
            return .inserted
        }
        guard let element else { return .failed }
        let outcome = insertViaAccessibility(trimmed, element: element, before: before, range: range)
        if outcome == .inserted { markInserted(trimmed) }
        return outcome
    }

    private static func markInserted(_ text: String) {
        lastInsertText = text
        lastInsertAt = CFAbsoluteTimeGetCurrent()
    }

    /// Prefer clipboard paste alone when AX verification is unreliable or the front app
    /// is known to accept both paste and AX (double-insert), or Electron reads paste late.
    private static func shouldTrustPasteWithoutAccessibility(before: String) -> Bool {
        if frontmostLooksLikeTelegram() { return true }
        // Cursor (ToDesktop/Electron): AX lags; long pasteLanded + early clipboard restore
        // raced so ⌘V could read the *old* pasteboard. Trust one ⌘V, skip AX.
        if frontmostLooksLikeCursor() { return true }
        // Empty AXValue is common for some composers while ⌘V still works; AX then duplicates.
        if before.isEmpty { return true }
        return false
    }

    private static func frontmostLooksLikeTelegram() -> Bool {
        guard let id = NSWorkspace.shared.frontmostApplication?.bundleIdentifier?.lowercased() else {
            return false
        }
        return id.contains("telegram")
    }

    /// Cursor ships as ToDesktop (`com.todesktop.230313mzl4w4u92`) named "Cursor".
    private static func frontmostLooksLikeCursor() -> Bool {
        guard let app = NSWorkspace.shared.frontmostApplication else { return false }
        let id = app.bundleIdentifier?.lowercased() ?? ""
        let name = (app.localizedName ?? "").lowercased()
        if name == "cursor" { return true }
        if id.contains("cursor") { return true }
        // Official Cursor.ai mac build (ToDesktop wrapper around Electron).
        if id == "com.todesktop.230313mzl4w4u92" { return true }
        return false
    }

    private static func insertViaAccessibility(
        _ text: String,
        element: AXUIElement,
        before: String?,
        range: NSRange?
    ) -> Outcome {
        // Paste may have landed while we decided to fall back — never insert twice.
        if let before, alreadyPresent(text, before: before, range: range, element: element) {
            AppLog.shared.write("INFO", "Текст уже в поле, Accessibility пропущен.")
            return .inserted
        }
        let setResult = AXUIElementSetAttributeValue(element, kAXSelectedTextAttribute as CFString, text as CFTypeRef)
        if setResult == .success {
            AppLog.shared.write("INFO", "Вставка через Accessibility.")
            return .inserted
        }
        if landed(text, before: before, range: range, element: element)
            || (before != nil && waitAndLanded(text, before: before, range: range, element: element)) {
            AppLog.shared.write("INFO", "Текст уже в поле после Accessibility (код \(setResult.rawValue)).")
            return .inserted
        }
        AppLog.shared.write("INFO", "Accessibility не изменила поле (код \(setResult.rawValue)).")
        return .failed
    }

    private static func focusedElement() -> AXUIElement? {
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        let copyResult = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused)
        guard copyResult == .success, let focused else { return nil }
        guard CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
        return (focused as! AXUIElement)
    }

    private static func isSecureTextField(_ element: AXUIElement) -> Bool {
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
        return (role as? String) == "AXSecureTextField"
    }

    private static func pasteLanded(_ text: String, before: String, range: NSRange?, element: AXUIElement) -> Bool {
        // ~0.4s: Telegram / Electron often update AX after the synthetic ⌘V event.
        for _ in 0..<8 {
            Thread.sleep(forTimeInterval: 0.05)
            if landed(text, before: before, range: range, element: element) { return true }
            if selectionAdvanced(text, from: range, element: element) { return true }
        }
        return false
    }

    private static func alreadyPresent(
        _ text: String,
        before: String,
        range: NSRange?,
        element: AXUIElement
    ) -> Bool {
        landed(text, before: before, range: range, element: element)
            || selectionAdvanced(text, from: range, element: element)
    }

    private static func selectionAdvanced(_ text: String, from range: NSRange?, element: AXUIElement) -> Bool {
        guard let range, let after = selectedRange(element) else { return false }
        let inserted = (text as NSString).length
        guard inserted > 0 else { return false }
        return after.length == 0 && after.location == range.location + inserted
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
        let beforeCount = occurrenceCount(of: text, in: before)
        let afterCount = occurrenceCount(of: text, in: after)
        // ≥ +1 covers a successful paste; ≥ +2 means already duplicated — still treat as landed
        // so we do not call Accessibility and make it worse.
        if afterCount >= beforeCount + 1 {
            return true
        }
        let added = (text as NSString).length
        guard added > 0, (after as NSString).length == (before as NSString).length + added else { return false }
        return false
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
        // Brief settle so the target reads the new clipboard before ⌘V (Telegram is sensitive).
        Thread.sleep(forTimeInterval: 0.03)
        postCommandV()
        // Electron (Cursor) often reads the pasteboard well after ⌘V; restoring at ~0.8s
        // raced with that read and pasted the *previous* clipboard. Keep changeCount so a
        // user copy meanwhile is never overwritten.
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) {
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
        // Explicit flags only — avoid Option/Shift left over from the dictation hotkey.
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
