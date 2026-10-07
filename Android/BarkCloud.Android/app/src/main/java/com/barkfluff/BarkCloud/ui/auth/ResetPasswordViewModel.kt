package com.barkfluff.BarkCloud.ui.auth

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.barkfluff.BarkCloud.data.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ResetPasswordUiState(
    val step: Int = 0,
    val login: String = "",
    val code: String = "",
    val password: String = "",
    val repeatedPassword: String = "",
    val revokeOtherSessions: Boolean = true,
    val loading: Boolean = false,
    val error: String? = null,
    val resendAt: Long = 0,
    val retryAt: Long = 0,
)

class ResetPasswordViewModel(
    private val repository: AuthFlowRepository,
    private val saved: SavedStateHandle = SavedStateHandle(),
    private val now: () -> Long = System::currentTimeMillis,
) : ViewModel() {
    private val mutable = MutableStateFlow(ResetPasswordUiState(login = saved["login"] ?: ""))
    val state = mutable.asStateFlow()
    private val completed = Channel<Unit>(Channel.BUFFERED)
    val events = completed.receiveAsFlow()
    private var resetId: String? = null

    fun loginChanged(value: String) { saved["login"] = value; change { it.copy(login = value) } }
    fun codeChanged(value: String) = change { it.copy(code = verificationDigits(value)) }
    fun passwordChanged(value: String) = change { it.copy(password = value) }
    fun repeatChanged(value: String) = change { it.copy(repeatedPassword = value) }
    fun revokeChanged(value: Boolean) = change { it.copy(revokeOtherSessions = value) }
    private fun change(transform: (ResetPasswordUiState) -> ResetPasswordUiState) {
        if (!state.value.loading) mutable.update { transform(it).copy(error = null) }
    }
    fun back(): Boolean {
        if (state.value.loading) return true
        if (state.value.step == 0) return false
        mutable.update { it.copy(step = it.step - 1, error = null) }
        return true
    }

    fun next() {
        val s = state.value
        if (s.loading || s.retryAt > now()) return
        when (s.step) {
            0 -> if (s.login.isBlank()) error("Введите логин или почту") else runOperation { sendCode() }
            1 -> if (s.code.length != 6) error("Введите 6 цифр кода") else mutable.update { it.copy(step = 2, error = null) }
            2 -> when {
                newPasswordError(s.password) != null -> error(newPasswordError(s.password)!!)
                s.password != s.repeatedPassword -> error("Пароли не совпадают")
                else -> runOperation {
                    repository.confirmPasswordReset(checkNotNull(resetId), s.code, s.password, s.revokeOtherSessions)
                    mutable.update { it.copy(password = "", repeatedPassword = "", code = "") }
                    completed.send(Unit)
                }
            }
        }
    }

    fun resend() {
        if (state.value.loading || state.value.resendAt > now() || state.value.retryAt > now()) return
        runOperation { sendCode() }
    }

    private suspend fun sendCode() {
        resetId = repository.requestPasswordReset(state.value.login.trim())
        mutable.update { it.copy(step = 1, code = "", resendAt = now() + 60_000) }
    }

    private fun runOperation(block: suspend () -> Unit) {
        mutable.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            try { block() }
            catch (e: AuthFlowException) {
                mutable.update { it.copy(error = e.message,
                    step = if (e.kind in setOf(AuthFailureKind.INVALID_CODE, AuthFailureKind.EXPIRED_CODE)) 1 else it.step,
                    retryAt = e.retryAfterSeconds?.let { seconds -> now() + seconds * 1000L } ?: it.retryAt,
                    resendAt = e.retryAfterSeconds?.let { seconds -> maxOf(it.resendAt, now() + seconds * 1000L) } ?: it.resendAt) }
            } finally { mutable.update { it.copy(loading = false) } }
        }
    }
    private fun error(text: String) { mutable.update { it.copy(error = text) } }
}
