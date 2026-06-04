import SwiftUI

/// Меню в строке состояния: открыть окно / подключение домена / выход.
struct MenuBarView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Открыть BarkCloud Drive") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Divider()
        if model.phase == .dashboard {
            if model.domain.isEnabled {
                Button("Открыть в Finder") { model.domain.revealInFinder() }
                Button("Отключить") { Task { await model.domain.disable() } }
            } else {
                Button("Подключить") { Task { await model.domain.enable() } }
            }
        }
        Divider()
        Button("Выход") { NSApplication.shared.terminate(nil) }
            .keyboardShortcut("q")
    }
}
