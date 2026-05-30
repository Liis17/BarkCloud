import SwiftUI

/// Полноэкранная заглушка между `LoginScreen` и `MainScreen` в [[RootView]]: пока
/// `AppLockManager.shouldShowLock` — пользователь видит этот экран. На входе
/// автоматически вызывается Face ID; при отказе/недоступности доступен ввод PIN.
struct AppLockScreen: View {
    @Environment(AppEnvironment.self) private var env

    @State private var didAutoPrompt = false
    @State private var showPinEntry = false
    @State private var isBiometricRunning = false
    @State private var errorMessage: String?

    private let pinLength = 6

    var body: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 24)
            header
            Spacer(minLength: 24)
            content
            Spacer(minLength: 24)
        }
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .task {
            guard !didAutoPrompt else { return }
            didAutoPrompt = true
            await tryBiometric()
        }
    }

    private var header: some View {
        VStack(spacing: 14) {
            Image(systemName: "lock.shield.fill")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.accent)
            Text("app_lock_screen_title")
                .font(AppTypography.titleLarge)
                .foregroundStyle(AppColors.onSurface)
        }
    }

    @ViewBuilder
    private var content: some View {
        if showPinEntry {
            PinEntryView(
                pinLength: pinLength,
                title: "app_lock_enter_pin",
                subtitle: nil,
                errorMessage: errorMessage,
                onSubmit: { pin in Task { await handlePin(pin) } }
            )
        } else {
            VStack(spacing: 12) {
                Button {
                    Task { await tryBiometric() }
                } label: {
                    Label(String(localized: "app_lock_unlock"), systemImage: biometricIconName)
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .tint(AppColors.accent)
                .controlSize(.large)
                .disabled(isBiometricRunning)

                Button(String(localized: "app_lock_use_pin")) {
                    showPinEntry = true
                }
                .font(AppTypography.titleSmall)
                .foregroundStyle(AppColors.accent)
            }
            .padding(.horizontal, 24)
        }
    }

    private var biometricIconName: String {
        switch env.appLock.biometric.availability() {
        case .faceID: return "faceid"
        case .touchID: return "touchid"
        default: return "lock.fill"
        }
    }

    private func tryBiometric() async {
        guard !isBiometricRunning else { return }
        isBiometricRunning = true
        let ok = await env.appLock.unlockWithBiometric(
            reason: String(localized: "app_lock_reason")
        )
        isBiometricRunning = false
        if !ok { showPinEntry = true }
    }

    private func handlePin(_ pin: String) async {
        let result = await env.appLock.verifyPin(pin)
        switch result {
        case .success, .wiped:
            // RootView переключит экран сам: либо .unlocked, либо нет токена → Login.
            break
        case .wrong(let remaining):
            errorMessage = String(
                format: NSLocalizedString("app_lock_wrong_pin", comment: ""),
                remaining
            )
        }
    }
}
