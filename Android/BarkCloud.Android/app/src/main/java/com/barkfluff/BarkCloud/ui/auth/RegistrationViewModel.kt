package com.barkfluff.BarkCloud.ui.auth

import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.barkfluff.BarkCloud.data.*
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

private val usernamePattern = Regex("[A-Za-z0-9._]+")
data class RegistrationUiState(
    val step: Int = 0,
    val firstName: String = "",
    val lastName: String = "",
    val username: String = "",
    val email: String = "",
    val password: String = "",
    val code: String = "",
    val usernameAvailable: Boolean? = null,
    val checkingUsername: Boolean = false,
    val pendingPassword: Boolean = false,
    val loading: Boolean = false,
    val error: String? = null,
    val resendAt: Long = 0,
    val retryAt: Long = 0,
)

class RegistrationViewModel(
    private val repository: AuthFlowRepository,
    private val saved: SavedStateHandle = SavedStateHandle(),
    private val now: () -> Long = System::currentTimeMillis,
) : ViewModel() {
    private val pending = repository.pendingRegistration()
    private val mutable = MutableStateFlow(RegistrationUiState(
        step = if (pending != null) 2 else 0,
        firstName = pending?.details?.firstName ?: saved["firstName"] ?: "",
        lastName = pending?.details?.lastName ?: saved["lastName"] ?: "",
        username = pending?.details?.username ?: saved["username"] ?: "",
        email = pending?.details?.email ?: saved["email"] ?: "",
        pendingPassword = pending != null,
    ))
    val state = mutable.asStateFlow()
    private var usernameJob: Job? = null
    private var codeId: String? = null
    private var finishAttempted = pending != null

    fun firstNameChanged(value: String) { saved["firstName"] = value; change { it.copy(firstName = value) } }
    fun lastNameChanged(value: String) { saved["lastName"] = value; change { it.copy(lastName = value) } }
    fun emailChanged(value: String) { saved["email"] = value; change { it.copy(email = value) } }
    fun passwordChanged(value: String) = change { it.copy(password = value) }
    fun codeChanged(value: String) = change { it.copy(code = verificationDigits(value)) }
    private fun change(transform: (RegistrationUiState) -> RegistrationUiState) {
        if (!state.value.loading) mutable.update { transform(it).copy(error = null) }
    }

    fun usernameChanged(value: String) {
        if (state.value.loading) return
        usernameJob?.cancel()
        saved["username"] = value
        mutable.update { it.copy(username = value, usernameAvailable = null, checkingUsername = false, error = null) }
        val username = value.trim()
        if (!usernamePattern.matches(username)) return
        if (state.value.retryAt > now()) { error("Слишком много проверок. Попробуйте позже."); return }
        usernameJob = viewModelScope.launch {
            mutable.update { it.copy(checkingUsername = true) }
            delay(400)
            try {
                val available = repository.usernameAvailable(username)
                if (state.value.username.trim() == username) mutable.update { it.copy(usernameAvailable = available, checkingUsername = false) }
            } catch (e: AuthFlowException) {
                if (state.value.username.trim() == username) mutable.update { it.copy(checkingUsername = false, error = e.message, retryAt = e.retryAfterSeconds?.let { seconds -> now() + seconds * 1000L } ?: it.retryAt) }
            }
        }
    }

    fun back(): Boolean {
        if (state.value.loading) return true
        if (state.value.pendingPassword || state.value.step == 0 || state.value.step == 4) return false
        mutable.update { it.copy(step = it.step - 1, error = null) }
        return true
    }

    fun next() {
        val s = state.value
        if (s.loading || s.retryAt > now()) return
        when (s.step) {
            0 -> if (s.firstName.isBlank()) error("Введите имя") else mutable.update { it.copy(step = 1, error = null) }.also {
                usernameChanged(s.username)
            }
            1 -> when {
                !usernamePattern.matches(s.username.trim()) -> error("Используйте латиницу, цифры, точку и подчёркивание")
                s.usernameAvailable != true -> error(if (s.checkingUsername) "Подождите проверки имени" else "Проверьте доступность имени пользователя")
                else -> mutable.update { it.copy(step = 2, error = null) }
            }
            2 -> {
                val passwordError = newPasswordError(s.password)
                when {
                    passwordError != null -> error(passwordError)
                    !s.pendingPassword && !validEmail(s.email) -> error("Введите корректную электронную почту")
                    s.pendingPassword -> runOperation { finish() }
                    else -> runOperation { create() }
                }
            }
            3 -> if (s.code.length != 6) error("Введите 6 цифр кода") else runOperation {
                repository.confirmAccount(details(), checkNotNull(codeId), state.value.code)
                mutable.update { it.copy(pendingPassword = true, code = "") }
                finish()
            }
        }
    }

    fun resend() {
        if (state.value.loading || state.value.resendAt > now() || state.value.retryAt > now()) return
        runOperation { create() }
    }

    private suspend fun create() {
        val challenge = repository.createAccount(details())
        codeId = challenge.codeId
        mutable.update { it.copy(resendAt = now() + 60_000, code = "", pendingPassword = challenge.needsPassword) }
        if (challenge.needsPassword) finish() else mutable.update { it.copy(step = 3) }
    }

    private suspend fun finish() {
        val verify = finishAttempted
        finishAttempted = true
        repository.finishRegistration(state.value.password, verify)
        mutable.update { it.copy(step = 4, password = "", code = "", pendingPassword = false) }
    }

    private fun runOperation(block: suspend () -> Unit) {
        mutable.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            try { block() }
            catch (e: AuthFlowException) {
                val pending = repository.pendingRegistration() != null
                mutable.update {
                    it.copy(error = e.message, pendingPassword = pending,
                        step = when { pending -> 2; e.kind == AuthFailureKind.USERNAME -> 1; e.kind == AuthFailureKind.EMAIL -> 2; else -> it.step },
                        usernameAvailable = if (e.kind == AuthFailureKind.USERNAME) false else it.usernameAvailable,
                        retryAt = e.retryAfterSeconds?.let { seconds -> now() + seconds * 1000L } ?: it.retryAt,
                        resendAt = e.retryAfterSeconds?.let { seconds -> maxOf(it.resendAt, now() + seconds * 1000L) } ?: it.resendAt)
                }
            } finally { mutable.update { it.copy(loading = false) } }
        }
    }

    private fun details() = state.value.let { RegistrationDetails(it.firstName.trim(), it.lastName.trim(), it.username.trim(), it.email.trim()) }
    private fun error(text: String) { mutable.update { it.copy(error = text) } }
}

fun validEmail(value: String): Boolean {
    val trimmed = value.trim()
    return trimmed.length <= 254 && Regex("[^\\s@]+@[^\\s@]+\\.[^\\s@]+").matches(trimmed)
}
