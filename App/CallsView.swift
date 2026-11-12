import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

struct MainWindow: View {
    @EnvironmentObject private var appState: AppState

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Picker("Раздел", selection: $appState.mainSection) {
                    ForEach(MainSection.allCases) { section in
                        Text(section.title).tag(section)
                    }
                }
                .pickerStyle(.segmented)
                .frame(width: 240)
                Spacer()
                if appState.mainSection == .calls {
                    Button("Открыть запись встречи…") {
                        appState.importMeetingRecording()
                    }
                    .disabled(!(appState.phase == .idle && appState.modelStatus.isReady))
                }
                Button {
                    appState.openSettings()
                } label: {
                    Image(systemName: "gearshape")
                }
                .buttonStyle(.borderless)
                .help("Настройки")
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            Divider()
            switch appState.mainSection {
            case .calls:
                CallsView()
            case .debug:
                DebugView()
            }
        }
        .frame(minWidth: 860, minHeight: 520)
    }
}

struct CallsView: View {
    @EnvironmentObject private var appState: AppState
    @State private var selection: UUID?
    @StateObject private var player = CallPlayer()

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                ForEach(appState.calls) { call in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(call.title)
                        Text(subtitle(call))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .tag(call.id)
                }
            }
            .navigationSplitViewColumnWidth(min: 220, ideal: 260)
        } detail: {
            if let call = selectedCall {
                CallDetail(call: call, player: player)
                    .environmentObject(appState)
            } else {
                VStack(spacing: 12) {
                    Image(systemName: "phone")
                        .font(.system(size: 36))
                        .foregroundStyle(.secondary)
                    Text("Нет записи")
                        .font(.title3)
                    Text("Начните запись звонка из меню в трее или откройте WAV/m4a.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Button("Открыть запись встречи…") {
                        appState.importMeetingRecording()
                    }
                    .disabled(!(appState.phase == .idle && appState.modelStatus.isReady))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .onAppear {
            if selection == nil {
                selection = appState.calls.first?.id
            }
        }
        .onChange(of: appState.calls.map(\.id)) { _, ids in
            if let selection, !ids.contains(selection) {
                self.selection = ids.first
            } else if selection == nil {
                selection = ids.first
            }
        }
    }

    private var selectedCall: CallRecord? {
        appState.calls.first { $0.id == selection }
    }

    private func subtitle(_ call: CallRecord) -> String {
        let length = Format.minutes(call.duration)
        if call.segments.isEmpty {
            return "\(length) · без расшифровки"
        }
        return "\(length) · \(call.segments.count) фраз"
    }
}

private struct CallDetail: View {
    @EnvironmentObject private var appState: AppState
    let call: CallRecord
    @ObservedObject var player: CallPlayer
    @State private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text(call.title)
                        .font(.title2.bold())
                    Text(detailLine)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button(player.isPlaying ? "Стоп" : "Слушать") {
                    player.toggle(mic: call.micURL, system: call.systemURL)
                }
                .disabled(!FileManager.default.fileExists(atPath: call.micURL.path)
                          && !FileManager.default.fileExists(atPath: call.systemURL.path))
                Button("Расшифровать снова") {
                    appState.retranscribe(call)
                }
                .disabled(appState.phase != .idle || !appState.modelStatus.isReady)
                Button("Экспорт…") {
                    export(call)
                }
                .disabled(call.segments.isEmpty)
                Button("Удалить", role: .destructive) {
                    confirmDelete = true
                }
            }

            if call.isMeeting {
                Text("Импорт с диктофона: одна дорожка, спикеры — Собеседник1, Собеседник2, …")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else if !call.hasSystemAudio {
                Text("Дорожка собеседника не записана. В настройках разрешите системный звук: видео экрана не сохраняется.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if call.segments.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "text.quote")
                        .font(.system(size: 32))
                        .foregroundStyle(.secondary)
                    Text("Расшифровки нет")
                        .font(.title3)
                    Text("Остановите запись из меню или нажмите «Расшифровать снова».")
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 10) {
                        ForEach(call.segments) { segment in
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                Text(Format.clock(segment.start))
                                    .font(.system(.body, design: .monospaced))
                                    .foregroundStyle(.secondary)
                                    .frame(width: 72, alignment: .leading)
                                Text(segment.speakerTitle)
                                    .font(.headline)
                                    .frame(width: 110, alignment: .leading)
                                Text(segment.text)
                                    .textSelection(.enabled)
                                Spacer(minLength: 0)
                            }
                        }
                    }
                    .padding(.vertical, 4)
                }
            }
        }
        .padding(20)
        .confirmationDialog("Удалить эту запись?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Удалить", role: .destructive) {
                player.stop()
                appState.deleteCall(call)
            }
        }
    }

    private var detailLine: String {
        let when = call.createdAt.formatted(date: .abbreviated, time: .shortened)
        return "\(when) · \(Format.minutes(call.duration))"
    }

    private func export(_ call: CallRecord) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.plainText]
        panel.nameFieldStringValue = "giga-call.txt"
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? call.plainTranscript.write(to: url, atomically: true, encoding: .utf8)
    }
}

final class CallPlayer: ObservableObject {
    @Published var isPlaying = false
    private var players: [AVAudioPlayer] = []
    private var timer: Timer?

    func play(_ url: URL) {
        if isPlaying {
            stop()
            return
        }
        guard FileManager.default.fileExists(atPath: url.path),
              let player = try? AVAudioPlayer(contentsOf: url) else {
            return
        }
        player.prepareToPlay()
        player.currentTime = 0
        player.play()
        players = [player]
        isPlaying = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: true) { [weak self] _ in
            guard let self else { return }
            if self.players.allSatisfy({ !$0.isPlaying }) {
                self.stop()
            }
        }
    }

    func toggle(mic: URL, system: URL) {
        if isPlaying {
            stop()
            return
        }
        players = [mic, system].compactMap { url in
            guard FileManager.default.fileExists(atPath: url.path),
                  let player = try? AVAudioPlayer(contentsOf: url) else {
                return nil
            }
            player.prepareToPlay()
            return player
        }
        guard !players.isEmpty else { return }
        for player in players {
            player.currentTime = 0
            player.play()
        }
        isPlaying = true
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: true) { [weak self] _ in
            guard let self else { return }
            if self.players.allSatisfy({ !$0.isPlaying }) {
                self.stop()
            }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        players.forEach { $0.stop() }
        players = []
        isPlaying = false
    }
}

struct DebugView: View {
    @EnvironmentObject private var appState: AppState
    @ObservedObject private var log = AppLog.shared
    @StateObject private var player = CallPlayer()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            status
            Divider()
            logPane
        }
    }

    private var status: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("Состояние")
                    .font(.headline)
                Spacer()
                Text(appState.phaseTitle)
                    .foregroundStyle(.secondary)
            }
            LabeledContent("Модель", value: appState.modelStatus.menuTitle)
            if let error = appState.lastError, !error.isEmpty {
                Text(error)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
            }
            LabeledContent("Последняя запись") {
                Text(captureSummary)
                    .textSelection(.enabled)
            }
            HStack {
                Button("Прослушать") { playLast() }
                    .disabled(appState.lastCaptureURL == nil)
                Button("Распознать ещё раз") { appState.retranscribeLastCapture() }
                    .disabled(appState.lastCaptureURL == nil || !appState.modelStatus.isReady)
                Button("Открыть WAV…") { openAudio() }
                    .disabled(!appState.modelStatus.isReady)
                Button("Вставить текст") { appState.insertDebugResult() }
                    .disabled(appState.debugResult.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            if !appState.debugResult.isEmpty {
                Text(appState.debugResult)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(8)
                    .background(Color(nsColor: .textBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
        }
        .padding(16)
    }

    private var logPane: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Журнал")
                    .font(.headline)
                Spacer()
                Button("Скопировать") { copyLog() }
                Button("Показать в Finder") { revealLog() }
                Button("Очистить") { log.clear() }
            }
            Text(log.fileURL.path)
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            ScrollView {
                Text(log.text.isEmpty ? "Журнал пуст." : log.text)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .padding(16)
    }

    private var captureSummary: String {
        guard let url = appState.lastCaptureURL else { return "ещё нет" }
        let seconds = String(format: "%.2f с", appState.lastCaptureDuration)
        let kilobytes = String(format: "%.1f КБ", Double(appState.lastCaptureBytes) / 1024)
        return "\(seconds), \(kilobytes), \(url.path)"
    }

    private func playLast() {
        guard let url = appState.lastCaptureURL else { return }
        player.play(url)
    }

    private func openAudio() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.wav, .audio]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.begin { response in
            guard response == .OK, let url = panel.url else { return }
            appState.transcribeDebugFile(url)
        }
    }

    private func copyLog() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(log.text, forType: .string)
    }

    private func revealLog() {
        NSWorkspace.shared.activateFileViewerSelecting([log.fileURL])
    }
}
