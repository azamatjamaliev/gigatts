import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var appState: AppState

    var body: some View {
        Form {
            Section("Распознавание") {
                LabeledContent("Статус", value: appState.modelStatus.menuTitle)
                if !appState.statusDetail.isEmpty {
                    Text(appState.statusDetail)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                Picker("Устройство", selection: Binding(
                    get: { appState.modelDevice },
                    set: { appState.setModelDevice($0) }
                )) {
                    Text("Процессор").tag("cpu")
                    Text("Графический процессор Apple").tag("mps")
                }
                .disabled(appState.phase != .idle)
                WorkerInstallControls()
                if case .failed = appState.modelStatus {
                    Button("Повторить загрузку") {
                        appState.reloadModel()
                    }
                    .disabled(appState.isInstalling)
                }
            }

            Section("Диктовка") {
                LabeledContent("Клавиша") {
                    HStack {
                        Text(appState.isCapturingHotkey ? "Нажмите сочетание…" : appState.hotkey.displayName)
                        Button(appState.isCapturingHotkey ? "Жду клавишу" : "Изменить") {
                            appState.beginHotkeyCapture()
                        }
                        Button("Правый Option") {
                            appState.resetHotkey()
                        }
                    }
                }
                Text("Удерживайте клавишу, говорите и отпустите — текст вставится в поле, где стоит курсор. Короткое нажатие короче 0,3 с игнорируется. Esc отменяет запись.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Picker("Микрофон", selection: Binding(
                    get: { appState.selectedDeviceUID },
                    set: { appState.setDeviceUID($0) }
                )) {
                    Text("Системный").tag("")
                    ForEach(appState.inputDevices) { device in
                        Text(device.name).tag(device.uid)
                    }
                }
            }

            Section("Разрешения") {
                PermissionRows()
            }

            Section("Приложение") {
                Toggle("Запускать при входе в систему", isOn: Binding(
                    get: { appState.launchAtLogin },
                    set: { appState.setLaunchAtLogin($0) }
                ))
                Text("Запись звонка начинается только по вашей команде. Предупредите собеседника, если этого требует ситуация. Звонки с iPhone это приложение не записывает.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 560, height: 640)
        .onAppear {
            appState.refreshPermissions()
            appState.refreshDevices()
            appState.refreshLoginItem()
        }
    }
}

struct OnboardingView: View {
    @EnvironmentObject private var appState: AppState
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("GigaПисец")
                    .font(.largeTitle.bold())
                Text("Локальная диктовка и расшифровка звонков. Речь распознаёт GigaAM v3 на этом Mac и никуда не отправляется.")
                    .foregroundStyle(.secondary)
                Text("По умолчанию диктовка — удержание правого Option. Внизу экрана на время записи мигает микрофон, затем текст вставляется в активное поле.")
                WorkerInstallControls()
                PermissionRows()
                Text("Запись звонка включается отдельно из меню. Предупредите собеседника. Для его голоса нужна запись системного звука: видео экрана не сохраняется.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                HStack {
                    Spacer()
                    Button("Продолжить") {
                        appState.finishOnboarding()
                        dismiss()
                    }
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(24)
        }
        .frame(minWidth: 480, minHeight: 560)
        .onAppear {
            appState.refreshPermissions()
        }
    }
}

struct WorkerInstallControls: View {
    @EnvironmentObject private var appState: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button(appState.isInstalling ? "Установка…" : "Установить распознавание") {
                appState.installWorker()
            }
            .disabled(!appState.canInstallWorker)
            Text("Ставит Python, ffmpeg, PyTorch, GigaAM и pyannote в папку данных пользователя. Приложение можно перенести в «Программы»: кнопка берёт файлы из самого GigaTTS.app. Интернет нужен для GigaAM. Модель разделения спикеров уже в приложении; HF_TOKEN нужен только если её нет в сборке.")
                .font(.callout)
                .foregroundStyle(.secondary)
            if !appState.installLog.isEmpty {
                ScrollViewReader { proxy in
                    ScrollView {
                        Text(appState.installLog)
                            .font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .textSelection(.enabled)
                        Color.clear.frame(height: 1).id("install-log-end")
                    }
                    .onChange(of: appState.installLog) { _, _ in
                        proxy.scrollTo("install-log-end", anchor: .bottom)
                    }
                }
                .frame(height: 220)
                .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
            }
        }
    }
}

struct PermissionRows: View {
    @EnvironmentObject private var appState: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            permissionRow("Микрофон", granted: appState.permissions.microphone) {
                Task {
                    _ = await Permissions.requestMicrophone()
                    appState.refreshPermissions()
                }
            }
            permissionRow("Мониторинг ввода", granted: appState.permissions.inputMonitoring) {
                Permissions.requestInputMonitoring()
                appState.refreshPermissions()
            }
            permissionRow("Универсальный доступ", granted: appState.permissions.accessibility) {
                Permissions.requestAccessibility()
                appState.refreshPermissions()
            }
            permissionRow("Системный звук", granted: appState.permissions.screenRecording) {
                Permissions.requestScreenRecording()
                appState.refreshPermissions()
            }
            Button("Проверить снова") {
                appState.refreshPermissions()
                appState.refreshDevices()
            }
        }
    }

    private func permissionRow(_ title: String, granted: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Image(systemName: granted ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(granted ? .green : .secondary)
            Text(title)
            Spacer()
            if !granted {
                Button("Разрешить", action: action)
            }
        }
    }
}
