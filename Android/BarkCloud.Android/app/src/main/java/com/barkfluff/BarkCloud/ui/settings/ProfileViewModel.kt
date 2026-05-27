package com.barkfluff.BarkCloud.ui.settings

import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.SessionManager
import com.barkfluff.BarkCloud.data.users.UserRepository
import com.barkfluff.BarkCloud.net.FileTransferService
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ProfileUiState(
    val isLoading: Boolean = true,
    val displayName: String = "",
    val username: String = "",
    val bio: String = "",
    val avatarUrl: String? = null,
    val hasAvatar: Boolean = false,
    val usedStorage: Long = 0,
    val storageLimit: Long = 0,
    val isUpdatingAvatar: Boolean = false,
    val isProcessing: Boolean = false,
    val snackbar: String? = null,
)

class ProfileViewModel(
    private val userRepository: UserRepository,
    private val transfer: FileTransferService,
    private val sessionManager: SessionManager,
) : ViewModel() {

    private val _state = MutableStateFlow(ProfileUiState())
    val state: StateFlow<ProfileUiState> = _state.asStateFlow()

    private val _events = Channel<ProfileEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val user = userRepository.getUser()
                val display = "${user.firstName} ${user.lastName}".trim()
                    .ifEmpty { user.username }
                val avatar = user.profilePicturePreview.ifEmpty { user.profilePicture }.ifEmpty { null }
                val storage = runCatching { transfer.storageInfo() }.getOrNull()
                _state.update {
                    it.copy(
                        isLoading = false,
                        displayName = display,
                        username = user.username,
                        bio = user.bio,
                        avatarUrl = avatar,
                        hasAvatar = avatar != null,
                        usedStorage = storage?.used ?: 0,
                        storageLimit = storage?.limit ?: 0,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun setAvatar(uri: Uri) {
        viewModelScope.launch {
            _state.update { it.copy(isUpdatingAvatar = true) }
            try {
                userRepository.setAvatar(uri, "avatar.jpg")
                _state.update { it.copy(isUpdatingAvatar = false) }
                load()
            } catch (e: Exception) {
                _state.update { it.copy(isUpdatingAvatar = false, snackbar = e.message) }
            }
        }
    }

    fun removeAvatar() {
        viewModelScope.launch {
            _state.update { it.copy(isUpdatingAvatar = true) }
            try {
                userRepository.removeAvatar()
                _state.update { it.copy(isUpdatingAvatar = false) }
                load()
            } catch (e: Exception) {
                _state.update { it.copy(isUpdatingAvatar = false, snackbar = e.message) }
            }
        }
    }

    fun signOut() {
        viewModelScope.launch {
            _state.update { it.copy(isProcessing = true) }
            sessionManager.signOut()
            _events.send(ProfileEvent.SignedOut)
        }
    }

    fun deleteAccount() {
        viewModelScope.launch {
            _state.update { it.copy(isProcessing = true) }
            // Чистим локально даже при ошибке сервера (как iOS): аккаунт мог уже удалиться.
            runCatching { userRepository.deleteAccount() }
            sessionManager.resetLocalState()
            _events.send(ProfileEvent.SignedOut)
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    sealed class ProfileEvent {
        data object SignedOut : ProfileEvent()
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return ProfileViewModel(app.userRepository, app.fileTransfer, app.sessionManager) as T
            }
        }
    }
}
