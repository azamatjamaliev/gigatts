import AppKit
import SwiftUI

final class PillModel: ObservableObject {
    enum Style: Equatable {
        case dictation
        case transcribing
        case call(String)
        case message(String)
    }

    @Published var style: Style = .dictation
}

struct PillView: View {
    @ObservedObject var model: PillModel

    var body: some View {
        HStack(spacing: 10) {
            switch model.style {
            case .dictation:
                Image(systemName: "mic.fill")
                    .font(.system(size: 22, weight: .semibold))
                    .modifier(BlinkModifier())
            case .transcribing:
                ProgressView()
                    .controlSize(.small)
                    .colorInvert()
                Text("Распознаю…")
            case .call(let time):
                Circle()
                    .fill(.red)
                    .frame(width: 10, height: 10)
                Text(time)
                    .monospacedDigit()
                    .font(.system(size: 16, weight: .semibold))
                Text("Звонок")
            case .message(let text):
                Text(text)
                    .lineLimit(2)
                    .multilineTextAlignment(.center)
            }
        }
        .font(.system(size: 15, weight: .medium))
        .foregroundStyle(.white)
        .padding(.horizontal, 18)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.black.opacity(0.84), in: Capsule())
        .padding(6)
    }
}

private struct BlinkModifier: ViewModifier {
    @State private var visible = true

    func body(content: Content) -> some View {
        content
            .opacity(visible ? 1 : 0.25)
            .onAppear {
                withAnimation(.easeInOut(duration: 0.45).repeatForever(autoreverses: true)) {
                    visible = false
                }
            }
    }
}

final class OverlayController {
    private let model = PillModel()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?

    func showDictation() {
        show(.dictation)
    }

    func showTranscribing() {
        show(.transcribing)
    }

    func showCall(elapsed: String) {
        show(.call(elapsed))
    }

    func showMessage(_ text: String, seconds: TimeInterval = 1.5) {
        show(.message(text))
        hideWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            if case .message = self.model.style {
                self.hide()
            }
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
    }

    func hide() {
        hideWork?.cancel()
        panel?.orderOut(nil)
    }

    private func show(_ style: PillModel.Style) {
        hideWork?.cancel()
        model.style = style
        let panel = ensurePanel()
        place(panel)
        panel.orderFrontRegardless()
    }

    private func ensurePanel() -> NSPanel {
        if let panel { return panel }
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 240, height: 64),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.statusWindow)))
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: PillView(model: model))
        host.frame = NSRect(x: 0, y: 0, width: 240, height: 64)
        panel.contentView = host
        self.panel = panel
        return panel
    }

    private func place(_ panel: NSPanel) {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 800, height: 600)
        let size = panel.frame.size
        let origin = NSPoint(x: visible.midX - size.width / 2, y: visible.minY + 28)
        panel.setFrameOrigin(origin)
    }
}
