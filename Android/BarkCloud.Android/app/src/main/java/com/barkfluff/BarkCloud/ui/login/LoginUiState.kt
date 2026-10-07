package com.barkfluff.BarkCloud.ui.login

data class LoginUiState(
    val login: String = "",
    val password: String = "",
    val otp: String = "",
    val otpRequired: Boolean = false,
    val isLoading: Boolean = false,
    val credentialsError: String? = null,
    val snackbarMessage: String? = null,
    val retryAtMillis: Long = 0L,
    val resendAtMillis: Long = 0L,
) {
    val canSubmit: Boolean
        get() = !isLoading &&
            login.isNotBlank() &&
            password.isNotEmpty() &&
            (!otpRequired || otp.length == OTP_LENGTH)

    companion object {
        const val OTP_LENGTH = 6
    }
}
