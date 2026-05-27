package com.barkfluff.BarkCloud.ui.settings

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
    val usernameTaken: Boolean = false,
    val snackbar: String? = null,
) {
    val bioTooLong: Boolean get() = bio.length > MAX_BIO
    val canSave: Boolean get() = !isLoading && !isSaving && !bioTooLong

    companion object {
        const val MAX_BIO = 200
    }
}

class EditProfileViewModel(
    private val userRepository: UserRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(EditProfileUiState())
    val state: StateFlow<EditProfileUiState> = _state.asStateFlow()

    private val _events = Channel<Unit>(Channel.BUFFERED)
    val saved = _events.receiveAsFlow()

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
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
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
                return EditProfileViewModel(app.userRepository) as T
            }
        }
    }
}
