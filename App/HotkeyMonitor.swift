import AppKit
import CoreGraphics

final class HotkeyMonitor {
    weak var appState: AppState?
    private(set) var isRunning = false
    private var port: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var modifierHeld = false
    private var captureSawNonModifier = false
    private var captureModifier: UInt16?

    func start() {
        stop()
        let mask = (1 << CGEventType.flagsChanged.rawValue)
            | (1 << CGEventType.keyDown.rawValue)
            | (1 << CGEventType.keyUp.rawValue)
        guard let port = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: CGEventMask(mask),
            callback: HotkeyMonitor.callback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else {
            isRunning = false
            return
        }
        self.port = port
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        isRunning = true
    }

    func stop() {
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let port {
            CGEvent.tapEnable(tap: port, enable: false)
        }
        runLoopSource = nil
        port = nil
        isRunning = false
        modifierHeld = false
    }

    func resetCapture() {
        captureSawNonModifier = false
        captureModifier = nil
    }

    private static let callback: CGEventTapCallBack = { _, type, event, userInfo in
        guard let userInfo else { return Unmanaged.passUnretained(event) }
        let monitor = Unmanaged<HotkeyMonitor>.fromOpaque(userInfo).takeUnretainedValue()
        return monitor.handle(type: type, event: event)
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let port {
                CGEvent.tapEnable(tap: port, enable: true)
            }
            return Unmanaged.passUnretained(event)
        }
        guard let appState else { return Unmanaged.passUnretained(event) }

        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let flags = event.flags

        if appState.isCapturingHotkey {
            if let captured = captureHotkey(type: type, keyCode: keyCode, flags: flags) {
                let hotkey = captured
                DispatchQueue.main.async {
                    appState.applyCapturedHotkey(hotkey)
                }
            }
            return nil
        }

        if type == .keyDown, keyCode == 0x35, appState.shouldCancelWithEscape {
            DispatchQueue.main.async {
                appState.cancelDictation()
            }
            return nil
        }

        let hotkey = appState.hotkey
        if hotkey.isModifier {
            guard type == .flagsChanged, keyCode == hotkey.keyCode else {
                return Unmanaged.passUnretained(event)
            }
            let down = Hotkey.isModifierDown(keyCode: keyCode, flags: flags)
            if down && !modifierHeld {
                modifierHeld = true
                DispatchQueue.main.async { appState.hotkeyPressed() }
                return nil
            }
            if !down && modifierHeld {
                modifierHeld = false
                DispatchQueue.main.async { appState.hotkeyReleased() }
                return nil
            }
            return Unmanaged.passUnretained(event)
        }

        guard keyCode == hotkey.keyCode else { return Unmanaged.passUnretained(event) }
        let mods = Hotkey.interestingModifiers(flags)
        guard mods == hotkey.modifierFlags else { return Unmanaged.passUnretained(event) }
        if type == .keyDown {
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return nil }
            DispatchQueue.main.async { appState.hotkeyPressed() }
            return nil
        }
        if type == .keyUp {
            DispatchQueue.main.async { appState.hotkeyReleased() }
            return nil
        }
        return Unmanaged.passUnretained(event)
    }

    private func captureHotkey(type: CGEventType, keyCode: UInt16, flags: CGEventFlags) -> Hotkey? {
        if type == .keyDown, !Hotkey.isModifierKeyCode(keyCode), keyCode != 0x35 {
            captureSawNonModifier = true
            return Hotkey(
                keyCode: keyCode,
                modifiers: Hotkey.interestingModifiers(flags).rawValue,
                isModifier: false
            )
        }
        if type == .flagsChanged, Hotkey.isModifierKeyCode(keyCode) {
            let down = Hotkey.isModifierDown(keyCode: keyCode, flags: flags)
            if down {
                captureModifier = keyCode
                return nil
            }
            if !captureSawNonModifier, captureModifier == keyCode {
                return Hotkey(keyCode: keyCode, modifiers: 0, isModifier: true)
            }
        }
        return nil
    }
}
