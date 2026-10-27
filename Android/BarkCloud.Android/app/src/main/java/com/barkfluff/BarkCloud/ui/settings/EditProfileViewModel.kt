package com.barkfluff.BarkCloud.ui.settings

import com.barkfluff.BarkCloud.data.SessionManager
import android.net.Uri
import kotlinx.coroutines.CancellationException
import java.util.concurrent.atomic.AtomicBoolean
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.users.UserRepository
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class EditProfileUiState(
    val isLoading: Boolean = true,
    val firstName: String = "",
    val lastName: String = "",
    val username: String = "",
    val bio: String = "",
    val originalUsername: String = "",
    val isSaving: Boolean = false,
    val isProcessing: Boolean = false,
    val avatarUrl: String? = null,
    val hasAvatar: Boolean = false,
    val isUpdatingAvatar: Boolean = false,
    val usernameTaken: Boolean = false,
    val snackbar: String? = null,
) {
    val displayName: String get() = "$firstName $lastName".trim().ifEmpty { username }
    val bioTooLong: Boolean get() = bio.length > MAX_BIO
    val canSave: Boolean get() = !isLoading && !isSaving && !isProcessing && !isUpdatingAvatar && !bioTooLong

    companion object {
        const val MAX_BIO = 200
    }
}

class EditProfileViewModel(
    private val userRepository: UserRepository,
    private val sessionManager: SessionManager,
) : ViewModel() {

    private val _state = MutableStateFlow(EditProfileUiState())
    val state: StateFlow<EditProfileUiState> = _state.asStateFlow()

    private val avatarUpdateInProgress = AtomicBoolean()

    private val _events = Channel<Unit>(Channel.BUFFERED)
    val saved = _events.receiveAsFlow()

    private val _signedOut = Channel<Unit>(Channel.BUFFERED)
    val signedOut = _signedOut.receiveAsFlow()

    fun signOut() {
        if (_state.value.isProcessing) return
        _state.update { it.copy(isProcessing = true) }
        viewModelScope.launch {
            sessionManager.signOut()
            _signedOut.send(Unit)
        }
    }

    fun deleteAccount() {
        if (_state.value.isProcessing) return
        _state.update { it.copy(isProcessing = true) }
        viewModelScope.launch {
            // Чистим локально даже при ошибке сервера (как iOS): аккаунт мог уже удалиться.
            runCatching { userRepository.deleteAccount() }
            sessionManager.resetLocalState()
            _signedOut.send(Unit)
        }
    }

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val user = userRepository.getUser()
                _state.update {
                    it.copy(
                        isLoading = false,
                        firstName = user.firstName,
                        lastName = user.lastName,
                        username = user.username,
                        bio = user.bio,
                        originalUsername = user.username,
                        avatarUrl = user.profilePicture.ifEmpty { user.profilePicturePreview }.ifEmpty { null },
                        hasAvatar = user.profilePicture.isNotEmpty() || user.profilePicturePreview.isNotEmpty(),
                    )
                }
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun setAvatar(uri: Uri, onFinished: () -> Unit = {}) = updateAvatar(onFinished) {
        userRepository.setAvatar(uri)
    }

    fun removeAvatar() = updateAvatar {
        userRepository.removeAvatar()
    }

    private fun updateAvatar(onFinished: () -> Unit = {}, update: suspend () -> Unit) {
        if (!avatarUpdateInProgress.compareAndSet(false, true)) {
            onFinished()
            return
        }
        _state.update { it.copy(isUpdatingAvatar = true) }
        viewModelScope.launch {
            try {
                update()
                val user = userRepository.getUser()
                _state.update {
                    it.copy(
                        avatarUrl = user.profilePicture.ifEmpty { user.profilePicturePreview }.ifEmpty { null },
                        hasAvatar = user.profilePicture.isNotEmpty() || user.profilePicturePreview.isNotEmpty(),
                    )
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            } finally {
                onFinished()
                avatarUpdateInProgress.set(false)
                _state.update { it.copy(isUpdatingAvatar = false) }
            }
        }
    }

    fun onFirstNameChange(v: String) = _state.update { it.copy(firstName = v) }
    fun onLastNameChange(v: String) = _state.update { it.copy(lastName = v) }
    fun onUsernameChange(v: String) =
        _state.update { it.copy(username = v.lowercase().trim(), usernameTaken = false) }

    fun onBioChange(v: String) = _state.update { it.copy(bio = v) }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    fun save() {
        val s = _state.value
        if (!s.canSave) return
        _state.update { it.copy(isSaving = true, usernameTaken = false) }
        viewModelScope.launch {
            try {
                val usernameChanged = s.username.isNotBlank() && s.username != s.originalUsername
                if (usernameChanged && userRepository.usernameExists(s.username)) {
                    _state.update { it.copy(isSaving = false, usernameTaken = true) }
                    return@launch
                }
                userRepository.changeName(s.firstName.trim(), s.lastName.trim())
                if (usernameChanged) userRepository.changeUsername(s.username)
                userRepository.changeBio(s.bio)
                _state.update { it.copy(isSaving = false) }
                _events.send(Unit)
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                _state.update { it.copy(isSaving = false, snackbar = e.message) }
            }
        }
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return EditProfileViewModel(app.userRepository, app.sessionManager) as T
            }
        }
    }
}
