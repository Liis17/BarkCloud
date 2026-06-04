import SwiftUI
import BarkCloudKit

/// Первый запуск: адрес self-hosted сервера BarkCloud (общий хост + порты сервисов
/// Identity/Users/Files + self-signed TLS). Сохраняет `ServerConfig` в App Group.
struct ServerSetupView: View {
    @Environment(AppModel.self) private var model

    @State private var host: String = ServerConfig.current.filesHost
    @State private var identityPort: String = String(ServerConfig.current.identityPort)
    @State private var usersPort: String = String(ServerConfig.current.usersPort)
    @State private var filesPort: String = String(ServerConfig.current.filesPort)
    @State private var useTLS: Bool = ServerConfig.current.useTLS
    @State private var allowSelfSigned: Bool = ServerConfig.current.allowSelfSigned

    var body: some View {
        Form {
            Section("Сервер BarkCloud") {
                TextField("Хост", text: $host)
                    .textContentType(.URL)
                HStack {
                    TextField("Identity", text: $identityPort)
                    TextField("Users", text: $usersPort)
                    TextField("Files", text: $filesPort)
                }
            }
            Section("Безопасность") {
                Toggle("TLS (https)", isOn: $useTLS)
                Toggle("Разрешить self-signed сертификат", isOn: $allowSelfSigned)
            }
            Button("Продолжить", action: save)
                .keyboardShortcut(.defaultAction)
                .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        .formStyle(.grouped)
        .padding()
        .navigationTitle("Настройка сервера")
    }

    private func save() {
        let h = host.trimmingCharacters(in: .whitespaces)
        let config = ServerConfig(
            identityHost: h, identityPort: Int(identityPort) ?? 7020,
            usersHost: h, usersPort: Int(usersPort) ?? 7021,
            filesHost: h, filesPort: Int(filesPort) ?? 7025,
            useTLS: useTLS, allowSelfSigned: allowSelfSigned
        )
        model.saveServer(config)
    }
}
