package com.barkfluff.BarkCloud.ui.login

data class LoginUiState(
    val login: String = "",
    val password: String = "",
    val passwordVisible: Boolean = false,
    val otp: String = "",
    val otpRequired: Boolean = false,
    val isLoading: Boolean = false,
    val credentialsError: String? = null,
    val snackbarMessage: String? = null,
    val serverHost: String = "",
    val serverIdentityPort: String = "",
    val serverUsersPort: String = "",
    val serverFilesPort: String = "",
) {
    val canSubmit: Boolean
        get() = !isLoading &&
            login.isNotBlank() &&
            password.isNotBlank() &&
            (!otpRequired || otp.length == OTP_LENGTH)

    companion object {
        const val OTP_LENGTH = 6
        const val PORT_MAX_LENGTH = 5
    }
}
