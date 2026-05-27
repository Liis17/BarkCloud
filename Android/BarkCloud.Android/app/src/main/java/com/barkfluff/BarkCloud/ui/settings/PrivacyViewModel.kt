package com.barkfluff.BarkCloud.ui.settings

import barkcloud.users.UsersApiOuterClass.PrivacySettings
import barkcloud.users.UsersApiOuterClass.PrivacyVisibility
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.users.UserRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class PrivacyUiState(
    val isLoading: Boolean = true,
    val settings: PrivacySettings? = null,
    val isSaving: Boolean = false,
    val snackbar: String? = null,
)

class PrivacyViewModel(
    private val userRepository: UserRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(PrivacyUiState())
    val state: StateFlow<PrivacyUiState> = _state.asStateFlow()

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val settings = userRepository.getPrivacySettings()
                _state.update { it.copy(isLoading = false, settings = settings) }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun setProfileVisibility(v: PrivacyVisibility) = applyChange { it.toBuilder().setProfileVisibility(v).build() }
    fun setEmailVisibility(v: PrivacyVisibility) = applyChange { it.toBuilder().setEmailVisibility(v).build() }
    fun setLastSeenVisibility(v: PrivacyVisibility) = applyChange { it.toBuilder().setLastSeenVisibility(v).build() }
    fun setSearchable(v: Boolean) = applyChange { it.toBuilder().setSearchableByUsername(v).build() }

    private fun applyChange(mutate: (PrivacySettings) -> PrivacySettings) {
        val current = _state.value.settings ?: return
        val updated = mutate(current)
        _state.update { it.copy(settings = updated, isSaving = true) }
        viewModelScope.launch {
            try {
                val saved = userRepository.updatePrivacySettings(updated)
                _state.update { it.copy(settings = saved, isSaving = false) }
            } catch (e: Exception) {
                _state.update { it.copy(settings = current, isSaving = false, snackbar = e.message) }
            }
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return PrivacyViewModel(app.userRepository) as T
            }
        }
    }
}
