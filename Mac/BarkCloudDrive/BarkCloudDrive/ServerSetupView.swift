import SwiftUI
import BarkCloudKit

/// Настройка адресов и TLS self-hosted сервера BarkCloud. Сохраняет `ServerConfig` в App Group.
struct ServerSetupView: View {
    @Environment(AppModel.self) private var model

    @State private var host: String = ServerConfig.current.filesHost
    @State private var identityPort: String = String(ServerConfig.current.identityPort)
    @State private var usersPort: String = String(ServerConfig.current.usersPort)
    @State private var filesPort: String = String(ServerConfig.current.filesPort)
    @State private var tlsBypass = TlsBypassConsent(configuration: ServerConfig.current)
    @State private var showTLSBypassConfirmation = false
    @State private var saving = false

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
                Toggle("TLS (https)", isOn: Binding(
                    get: { tlsBypass.isTLSEnabled },
                    set: { tlsBypass.setTLSEnabled($0) }
                ))
                if tlsBypass.isTLSEnabled {
                    Toggle("Отключить проверку TLS-сертификата (небезопасно)", isOn: Binding(
                        get: { tlsBypass.isBypassEnabled },
                        set: { enabled in
                            if enabled {
                                showTLSBypassConfirmation = true
                            } else {
                                tlsBypass.disableBypass()
                            }
                        }
                    ))
                    if tlsBypass.isBypassEnabled {
                        Text("Проверка сертификатов отключена. Приложение принимает любой сертификат от указанных серверов, поэтому передаваемые данные могут быть перехвачены.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
            }
            Button("Продолжить") { Task { await save() } }
                .keyboardShortcut(.defaultAction)
                .disabled(saving || host.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        .formStyle(.grouped)
        .padding()
        .navigationTitle("Настройка сервера")
        .onChange(of: addressFields) { _, _ in updateAddressConsent() }
        .alert("Отключить проверку сертификата?", isPresented: $showTLSBypassConfirmation) {
            Button("Отмена", role: .cancel) {}
                .keyboardShortcut(.defaultAction)
            Button("Отключить проверку", role: .destructive) {
                tlsBypass.confirmBypass()
            }
        } message: {
            Text("Приложение будет принимать любой сертификат для указанных серверов. Злоумышленник может перехватить пароль, коды подтверждения, токены и файлы. Продолжайте, только если понимаете этот риск")
        }
    }

    private var addressFields: [String] { [host, identityPort, usersPort, filesPort] }

    private func updateAddressConsent() {
        tlsBypass.updateAddresses(
            identityHost: host,
            identityPort: identityPort,
            usersHost: host,
            usersPort: usersPort,
            filesHost: host,
            filesPort: filesPort
        )
    }

    private func save() async {
        saving = true
        let h = host.trimmingCharacters(in: .whitespaces)
        var config = ServerConfig(
            identityHost: h, identityPort: Int(identityPort) ?? 7020,
            usersHost: h, usersPort: Int(usersPort) ?? 7021,
            filesHost: h, filesPort: Int(filesPort) ?? 7025,
            useTLS: tlsBypass.isTLSEnabled,
            allowSelfSigned: tlsBypass.isBypassEnabled
        )
        config.allowSelfSigned = tlsBypass.allowsBypass(for: config)
        await model.saveServer(config)
        saving = false
    }
}
