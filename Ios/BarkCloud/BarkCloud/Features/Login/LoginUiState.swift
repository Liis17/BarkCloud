import Foundation

struct LoginUiState: Equatable {
    static let otpLength = 6

    var login: String = ""
    var password: String = ""
    var passwordVisible: Bool = false
    var otp: String = ""
    var otpRequired: Bool = false
    var isLoading: Bool = false
    var credentialsError: String? = nil
    var snackbarMessage: String? = nil

    var canSubmit: Bool {
        if isLoading { return false }
        if otpRequired {
            return otp.count == LoginUiState.otpLength
        }
        return !login.trimmingCharacters(in: .whitespaces).isEmpty && !password.isEmpty
    }
}
