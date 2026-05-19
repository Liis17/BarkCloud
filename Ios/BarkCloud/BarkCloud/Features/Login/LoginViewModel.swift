import Foundation
import Observation

@MainActor
@Observable
final class LoginViewModel {
    var state = LoginUiState()
    var navigateToMainRequest: Bool = false

    private let auth: AuthRepository

    init(auth: AuthRepository) {
        self.auth = auth
    }

    func onLoginChange(_ value: String) {
        state.login = value
        state.credentialsError = nil
    }

    func onPasswordChange(_ value: String) {
        state.password = value
        state.credentialsError = nil
    }

    func togglePasswordVisibility() {
        state.passwordVisible.toggle()
    }

    func onOtpChange(_ value: String) {
        let filtered = String(value.filter(\.isNumber).prefix(LoginUiState.otpLength))
        state.otp = filtered
        if filtered.count == LoginUiState.otpLength {
            submit()
        }
    }

    func onComingSoon() {
        state.snackbarMessage = String(localized: "login_coming_soon")
    }

    func snackbarShown() {
        state.snackbarMessage = nil
    }

    func submit() {
        guard state.canSubmit else { return }
        state.isLoading = true
        state.credentialsError = nil
        let loginValue = state.login.trimmingCharacters(in: .whitespaces)
        let passwordValue = state.password
        let otpValue: String? = state.otpRequired ? state.otp : nil

        Task { [auth] in
            let result = await auth.auth(login: loginValue, password: passwordValue, otpCode: otpValue)
            await MainActor.run {
                self.handle(result)
            }
        }
    }

    private func handle(_ result: AuthResult) {
        state.isLoading = false
        switch result {
        case .success:
            navigateToMainRequest = true
        case .otpRequired:
            state.otpRequired = true
            state.otp = ""
        case .invalidCredentials:
            state.credentialsError = String(localized: "login_error_invalid_credentials")
        case .otherError(let message):
            state.snackbarMessage = message.isEmpty ? String(localized: "login_error_network") : message
        }
    }
}
