import AppKit
import ApplicationServices
import AVFoundation
import CoreGraphics

enum Permissions {
    static func snapshot() -> PermissionSnapshot {
        PermissionSnapshot(
            microphone: AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
            inputMonitoring: CGPreflightListenEventAccess(),
            accessibility: AXIsProcessTrusted(),
            screenRecording: CGPreflightScreenCaptureAccess()
        )
    }

    static func requestMicrophone() async -> Bool {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        if status == .authorized { return true }
        if status == .notDetermined {
            return await withCheckedContinuation { continuation in
                AVCaptureDevice.requestAccess(for: .audio) { granted in
                    continuation.resume(returning: granted)
                }
            }
        }
        openPrivacy("Microphone")
        return false
    }

    static func requestInputMonitoring() {
        _ = CGRequestListenEventAccess()
        if !CGPreflightListenEventAccess() {
            openPrivacy("ListenEvent")
        }
    }

    static func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        let options = [key: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            openPrivacy("Accessibility")
        }
    }

    static func requestScreenRecording() {
        if !CGRequestScreenCaptureAccess() {
            openPrivacy("ScreenCapture")
        }
    }

    static func openPrivacy(_ name: String) {
        let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_\(name)")!
        NSWorkspace.shared.open(url)
    }
}
