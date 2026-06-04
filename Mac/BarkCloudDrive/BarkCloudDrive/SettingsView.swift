import SwiftUI
import ServiceManagement

/// Настройки: автозапуск (Login Item), выход из аккаунта, смена адреса сервера.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Настройки").font(.title2).bold()
                Spacer()
                Button("Готово") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding()

            Form {
                Section("Общие") {
                    Toggle("Запускать при входе в систему", isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, on in setLaunchAtLogin(on) }
                    LabeledContent("Точка монтирования", value: model.mount.mountPoint.path)
                }
                Section("Аккаунт") {
                    Button("Выйти из аккаунта", role: .destructive) {
                        Task { await model.logout(); dismiss() }
                    }
                    Button("Сменить адрес сервера") {
                        Task { await model.forgetServer(); dismiss() }
                    }
                }
            }
            .formStyle(.grouped)
        }
        .frame(width: 460, height: 360)
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch {
            launchAtLogin = SMAppService.mainApp.status == .enabled
        }
    }
}
