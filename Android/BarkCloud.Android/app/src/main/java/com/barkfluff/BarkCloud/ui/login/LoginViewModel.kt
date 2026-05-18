package com.barkfluff.BarkCloud.ui.login

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.AuthRepository
import com.barkfluff.BarkCloud.data.AuthResult
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
        _state.update { it.copy(login = value, credentialsError = null) }
    }

    fun onPasswordChange(value: String) {
        _state.update { it.copy(password = value, credentialsError = null) }
    }

    fun onPasswordVisibilityToggle() {
        _state.update { it.copy(passwordVisible = !it.passwordVisible) }
    }

    fun onOtpChange(value: String) {
        val sanitized = value.filter { it.isDigit() }.take(LoginUiState.OTP_LENGTH)
        _state.update { it.copy(otp = sanitized) }
        if (sanitized.length == LoginUiState.OTP_LENGTH) {
            submit()
        }
    }

    fun onComingSoon() {
        _state.update { it.copy(snackbarMessage = COMING_SOON) }
    }

    fun snackbarShown() {
        _state.update { it.copy(snackbarMessage = null) }
    }

    fun submit() {
        val current = _state.value
        if (!current.canSubmit) return
        _state.update { it.copy(isLoading = true, credentialsError = null) }

        viewModelScope.launch {
            val result = authRepository.auth(
                login = current.login.trim(),
                password = current.password,
                otpCode = current.otp.takeIf { current.otpRequired },
            )
            when (result) {
                AuthResult.Success -> {
                    _state.update { it.copy(isLoading = false) }
                    _events.send(LoginEvent.NavigateToMain)
                }
                AuthResult.OtpRequired -> {
                    _state.update {
                        it.copy(
                            isLoading = false,
                            otpRequired = true,
                            otp = "",
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
        private const val COMING_SOON = "Скоро"
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
