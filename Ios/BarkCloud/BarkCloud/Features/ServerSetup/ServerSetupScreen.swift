import SwiftUI

/// Ввод адресов микросервисов self-hosted сервера. Показывается на первом запуске
/// (через `RootView`, пока `ServerConfig` не настроен) и доступен с экрана логина
/// для исправления неверного адреса. Стиль повторяет `LoginScreen`.
struct ServerSetupScreen: View {
    @Environment(AppEnvironment.self) private var env

    /// Ненил — экран открыт как лист (с кнопкой «Закрыть»). Нил — первый запуск.
    var onCancel: (() -> Void)? = nil
    var onComplete: () -> Void = {}

    @State private var identityHost = ""
    @State private var identityPort = ""
    @State private var usersHost = ""
    @State private var usersPort = ""
    @State private var filesHost = ""
    @State private var filesPort = ""
    @State private var useTLS = true
    @State private var allowSelfSigned = true
    @State private var loaded = false
    @State private var showError = false
    @State private var saving = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                if let onCancel {
                    HStack {
                        Spacer()
                        Button("server_setup_close") { onCancel() }
                            .font(AppTypography.labelLarge)
                    }
                }

                VStack(alignment: .leading, spacing: 8) {
                    Text("server_setup_title")
                        .font(AppTypography.displaySmall)
                    Text("server_setup_subtitle")
                        .font(AppTypography.bodyMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .padding(.top, onCancel == nil ? 32 : 0)

                serviceField(
                    title: "server_setup_identity",
                    host: $identityHost, port: $identityPort
                )
                serviceField(
                    title: "server_setup_users",
                    host: $usersHost, port: $usersPort
                )
                serviceField(
                    title: "server_setup_files",
                    host: $filesHost, port: $filesPort
                )

                VStack(alignment: .leading, spacing: 12) {
                    Toggle("server_setup_tls", isOn: $useTLS)
                        .font(AppTypography.bodyLarge)
                        .tint(AppColors.accent)
                    if useTLS {
                        Toggle("server_setup_self_signed", isOn: $allowSelfSigned)
                            .font(AppTypography.bodyLarge)
                            .tint(AppColors.accent)
                    }
                }

                if showError {
                    Text("server_setup_invalid")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.error)
                }

                continueButton
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 32)
            .animation(.easeInOut(duration: 0.2), value: useTLS)
        }
        .onAppear(perform: loadIfNeeded)
    }

    @ViewBuilder
    private func serviceField(title: LocalizedStringKey, host: Binding<String>, port: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(AppTypography.titleMedium)
            HStack(spacing: 8) {
                TextField(String(localized: "server_setup_host"), text: host)
                    .textFieldStyle(.roundedBorder)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                TextField(String(localized: "server_setup_port"), text: port)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 88)
                    .keyboardType(.numberPad)
            }
        }
    }

    @ViewBuilder
    private var continueButton: some View {
        Button {
            Task { await submit() }
        } label: {
            HStack {
                if saving {
                    ProgressView().tint(.white)
                } else {
                    Text("server_setup_continue")
                        .font(AppTypography.titleMedium)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .disabled(saving)
    }

    private func loadIfNeeded() {
        guard !loaded else { return }
        let c = env.serverConfig.config
        identityHost = c.identityHost
        identityPort = String(c.identityPort)
        usersHost = c.usersHost
        usersPort = String(c.usersPort)
        filesHost = c.filesHost
        filesPort = String(c.filesPort)
        useTLS = c.useTLS
        allowSelfSigned = c.allowSelfSigned
        loaded = true
    }

    private func submit() async {
        guard let identity = port(identityPort),
              let users = port(usersPort),
              let files = port(filesPort),
              let iHost = host(identityHost),
              let uHost = host(usersHost),
              let fHost = host(filesHost) else {
            showError = true
            return
        }
        showError = false
        saving = true
        let config = ServerConfig(
            identityHost: iHost, identityPort: identity,
            usersHost: uHost, usersPort: users,
            filesHost: fHost, filesPort: files,
            useTLS: useTLS, allowSelfSigned: useTLS ? allowSelfSigned : false
        )
        env.serverConfig.save(config)
        // Сбросить закэшированные gRPC-соединения к старому адресу.
        await env.grpcManager.shutdown()
        saving = false
        onComplete()
    }

    /// Нормализованный хост: без схемы, слешей и пробелов. Нил — пустой.
    private func host(_ raw: String) -> String? {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        for prefix in ["https://", "http://"] where s.hasPrefix(prefix) {
            s.removeFirst(prefix.count)
        }
        s = s.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return s.isEmpty ? nil : s
    }

    private func port(_ raw: String) -> Int? {
        guard let v = Int(raw.trimmingCharacters(in: .whitespaces)), (1...65535).contains(v) else { return nil }
        return v
    }
}
