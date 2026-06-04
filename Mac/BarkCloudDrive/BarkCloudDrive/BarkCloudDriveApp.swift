import SwiftUI

/// Контейнер-приложение macOS-клиента BarkCloud: окно (server setup → логин →
/// дашборд) + значок в menu-bar. FSKit-расширение `BarkCloudFS` встроено в этот
/// бандл; монтирование инициируется отсюда (`MountManager`).
@main
struct BarkCloudDriveApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup("BarkCloud Drive", id: "main") {
            RootView()
                .environment(model)
                .frame(minWidth: 440, minHeight: 360)
        }
        .windowResizability(.contentSize)

        MenuBarExtra("BarkCloud Drive", systemImage: "externaldrive.badge.icloud") {
            MenuBarView()
                .environment(model)
        }
    }
}

/// Гейт: нет адреса сервера → ServerSetup; нет сессии → Login; иначе → Dashboard.
struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            switch model.phase {
            case .serverSetup: ServerSetupView()
            case .login: LoginView()
            case .dashboard: DashboardView()
            }
        }
        .animation(.default, value: model.phase)
    }
}
