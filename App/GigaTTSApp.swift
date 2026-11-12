import AppKit
import SwiftUI

@main
struct GigaTTSApp: App {
    @StateObject private var appState = AppState()

    var body: some Scene {
        MenuBarExtra {
            MenuContent()
                .environmentObject(appState)
        } label: {
            StatusItemLabel()
                .environmentObject(appState)
        }
        .menuBarExtraStyle(.menu)

        // Dedicated window (not Settings scene): showSettingsWindow: is unreliable for LSUIElement / .accessory.
        Window("Настройки", id: "settings") {
            SettingsView()
                .environmentObject(appState)
        }
        .windowResizability(.contentSize)
        .defaultSize(width: 560, height: 640)

        Window("GigaПисец", id: "main") {
            MainWindow()
                .environmentObject(appState)
        }
        .defaultSize(width: 980, height: 640)

        Window("Добро пожаловать", id: "onboarding") {
            OnboardingView()
                .environmentObject(appState)
        }
        .defaultSize(width: 520, height: 720)
    }
}

private struct StatusItemLabel: View {
    @EnvironmentObject private var appState: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: appState.symbolName)
            .onAppear {
                appState.onNeedOnboarding = {
                    appState.revealForUI()
                    openWindow(id: "onboarding")
                }
                appState.onOpenCalls = {
                    appState.revealForUI()
                    openWindow(id: "main")
                }
                appState.onOpenSettings = {
                    appState.revealForUI()
                    openWindow(id: "settings")
                }
                appState.bootstrap()
            }
    }
}

struct MenuContent: View {
    @EnvironmentObject private var appState: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(appState.modelStatus.menuTitle)
        if !appState.statusDetail.isEmpty, !appState.modelStatus.isReady {
            Text(appState.statusDetail)
        }
        Divider()

        if !appState.modelStatus.isReady {
            Button(appState.isInstalling ? "Установка распознавания…" : "Установить распознавание") {
                appState.openSettings()
                appState.installWorker()
            }
            .disabled(!appState.canInstallWorker)
        }

        Button(dictationTitle) {
            appState.toggleHandsFree()
        }
        .disabled(!appState.canStartDictation && !isDictating)

        Button(callTitle) {
            appState.toggleCall()
        }
        .disabled(!canToggleCall)

        Button("Открыть запись встречи…") {
            appState.importMeetingRecording()
        }
        .disabled(!canImportMeeting)

        if !appState.lastTranscript.isEmpty {
            Divider()
            Button("Скопировать последнюю фразу") {
                appState.copyLastTranscript()
            }
        }

        if !appState.dictationHistory.isEmpty {
            Menu("Недавние") {
                ForEach(Array(appState.dictationHistory.prefix(8))) { entry in
                    Button(entry.preview) {
                        appState.insertHistory(entry)
                    }
                }
            }
        }

        if let error = appState.lastError, !error.isEmpty {
            Divider()
            Text(error)
        }

        Divider()
        Button("GigaПисец…") {
            appState.revealForUI()
            openWindow(id: "main")
        }
        Button("Настройки…") {
            appState.openSettings()
        }
        Divider()
        Button("Выйти") {
            NSApp.terminate(nil)
        }
    }

    private var isDictating: Bool {
        if case .dictating = appState.phase { return true }
        return false
    }

    private var dictationTitle: String {
        isDictating ? "Остановить диктовку" : "Диктовка (\(appState.hotkey.displayName))"
    }

    private var callTitle: String {
        if case .recordingCall = appState.phase {
            return "Остановить и расшифровать"
        }
        return "Начать запись звонка"
    }

    private var canToggleCall: Bool {
        if case .recordingCall = appState.phase { return true }
        return appState.phase == .idle && appState.modelStatus.isReady
    }

    private var canImportMeeting: Bool {
        appState.phase == .idle && appState.modelStatus.isReady
    }
}
