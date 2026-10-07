package com.barkfluff.BarkCloud.ui.login

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.AuthResult
import com.barkfluff.BarkCloud.data.verificationDigits
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

class LoginViewModel(
    private val authRepository: AuthRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(LoginUiState())
    val state: StateFlow<LoginUiState> = _state.asStateFlow()

    private val _events = Channel<LoginEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    fun onLoginChange(value: String) {
        _state.update { if (it.isLoading) it else it.copy(login = value, credentialsError = null, snackbarMessage = null) }
    }

    fun onPasswordChange(value: String) {
        _state.update { if (it.isLoading) it else it.copy(password = value, credentialsError = null, snackbarMessage = null) }
    }

    fun onOtpChange(value: String) {
        val sanitized = verificationDigits(value)
        _state.update { if (it.isLoading) it else it.copy(otp = sanitized, credentialsError = null, snackbarMessage = null) }
    }

    fun backFromOtp() {
        if (!_state.value.isLoading) _state.update { it.copy(otpRequired = false, otp = "", credentialsError = null, snackbarMessage = null) }
    }

    fun submit() = performSubmit()

    fun resendOtp() {
        val current = state.value
        if (current.otpRequired && current.resendAtMillis <= System.currentTimeMillis()) performSubmit(resend = true)
    }

    private fun performSubmit(resend: Boolean = false) {
        val current = _state.value
        if (!(if (resend) current.copy(otpRequired = false).canSubmit else current.canSubmit) || current.retryAtMillis > System.currentTimeMillis()) return
        _state.update { it.copy(isLoading = true, credentialsError = null) }

        viewModelScope.launch {
            val result = authRepository.auth(
                login = current.login.trim(),
                password = current.password,
                otpCode = current.otp.takeIf { current.otpRequired && !resend },
            )
            when (result) {
                AuthResult.Success -> {
                    _state.update { it.copy(isLoading = false, password = "", otp = "") }
                    _events.send(LoginEvent.NavigateToMain)
                }
                AuthResult.OtpRequired -> {
                    _state.update {
                        it.copy(
                            isLoading = false,
                            otpRequired = true,
                            otp = "",
                            resendAtMillis = System.currentTimeMillis() + 60_000,
                        )
                    }
                }
                AuthResult.InvalidCredentials -> {
                    _state.update {
                        it.copy(
                            isLoading = false,
                            credentialsError = INVALID_CREDENTIALS,
                        )
                    }
                }
                is AuthResult.OtherError -> {
                    _state.update {
                        it.copy(
                            isLoading = false,
                            snackbarMessage = result.message.ifBlank { NETWORK_ERROR },
                            retryAtMillis = result.retryAfterSeconds?.let { System.currentTimeMillis() + it * 1000L } ?: 0L,
                        )
                    }
                }
            }
        }
    }

    sealed class LoginEvent {
        data object NavigateToMain : LoginEvent()
    }

    companion object {
        private const val INVALID_CREDENTIALS = "Неверный логин или пароль"
        private const val NETWORK_ERROR = "Не удалось связаться с сервером"

        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return LoginViewModel(app.authRepository) as T
            }
        }
    }
}
