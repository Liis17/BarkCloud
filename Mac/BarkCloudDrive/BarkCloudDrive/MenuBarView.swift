import SwiftUI

/// Меню в строке состояния: открыть окно / монтаж / размонтаж / выход.
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
            if model.mount.isMounted {
                Button("Размонтировать") { Task { await model.mount.unmount() } }
            } else {
                Button("Примонтировать") { Task { await model.mount.mount() } }
            }
        }
        Divider()
        Button("Выход") { NSApplication.shared.terminate(nil) }
            .keyboardShortcut("q")
    }
}
