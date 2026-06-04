import SwiftUI

/// Контейнер-приложение macOS-клиента (Этап 2 — server setup, логин, монтаж,
/// дашборд). Пока заглушка: FSKit-расширение `BarkCloudFS` собирается и встраивается
/// в этот бандл; реальный UI и монтирование добавляются на Этапе 2.
@main
struct BarkCloudDriveApp: App {
    var body: some Scene {
        WindowGroup("BarkCloud Drive") {
            ContentView()
        }
        .windowResizability(.contentSize)
    }
}

struct ContentView: View {
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "externaldrive.badge.icloud")
                .font(.system(size: 48))
            Text("BarkCloud Drive")
                .font(.title2).bold()
            Text("FSKit-том. UI и монтирование — Этап 2.")
                .foregroundStyle(.secondary)
        }
        .padding(40)
        .frame(minWidth: 360, minHeight: 240)
    }
}
