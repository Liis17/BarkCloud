import SwiftUI

/// Настройки → Приложение. Сейчас один пункт: переключатель блокировки на входе
/// (Face ID + резервный PIN). Включение требует биометрии и задания PIN через
/// [[SetPinSheet]], отключение — биометрии.
struct AppSettingsScreen: View {
    @Environment(AppEnvironment.self) private var env

    @State private var showSetPin = false
    @State private var snackbar: String?
    @State private var pendingEnable = false

    var body: some View {
        List {
            Section {
                Toggle(isOn: lockToggle) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("app_lock_settings_title")
                            .font(AppTypography.titleMedium)
                            .foregroundStyle(AppColors.onSurface)
                        Text(subtitleKey)
                            .font(AppTypography.bodySmall)
                            .foregroundStyle(AppColors.onSurfaceVariant)
                    }
                }
                .tint(AppColors.accent)
                .disabled(!biometricAvailable && !env.appLock.settings.isEnabled)
            } footer: {
                if !biometricAvailable {
                    Text("app_lock_unavailable")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.error)
                } else {
                    Text("app_lock_footer")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
        }
        .navigationTitle(String(localized: "settings_app"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showSetPin) {
            SetPinSheet(
                pinLength: 6,
                onComplete: { pin in
                    env.appLock.settings.enable(pin: pin)
                    showSetPin = false
                    snackbar = String(localized: "app_lock_enabled")
                },
                onCancel: {
                    showSetPin = false
                    pendingEnable = false
                }
            )
            .interactiveDismissDisabled()
        }
        .overlay(alignment: .bottom) { snackbarView }
    }

    private var subtitleKey: LocalizedStringResource {
        env.appLock.settings.isEnabled ? "app_lock_settings_on" : "app_lock_settings_off"
    }

    private var biometricAvailable: Bool {
        switch env.appLock.biometric.availability() {
        case .faceID, .touchID, .passcodeOnly: return true
        case .unavailable: return false
        }
    }

    /// Кастомный binding: переключение требует Face ID и (на включение) задания PIN.
    private var lockToggle: Binding<Bool> {
        Binding(
            get: { env.appLock.settings.isEnabled || pendingEnable },
            set: { wantEnabled in
                if wantEnabled {
                    Task { await beginEnable() }
                } else {
                    Task { await beginDisable() }
                }
            }
        )
    }

    private func beginEnable() async {
        let ok = await env.appLock.biometric.authenticate(
            reason: String(localized: "app_lock_enable_reason")
        )
        guard ok else {
            snackbar = String(localized: "app_lock_biometric_failed")
            return
        }
        pendingEnable = true
        showSetPin = true
    }

    private func beginDisable() async {
        let ok = await env.appLock.biometric.authenticate(
            reason: String(localized: "app_lock_disable_reason")
        )
        guard ok else {
            snackbar = String(localized: "app_lock_biometric_failed")
            return
        }
        env.appLock.settings.disable()
        snackbar = String(localized: "app_lock_disabled")
    }

    @ViewBuilder
    private var snackbarView: some View {
        if let text = snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, 16)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        snackbar = nil
                    }
                }
        }
    }
}
