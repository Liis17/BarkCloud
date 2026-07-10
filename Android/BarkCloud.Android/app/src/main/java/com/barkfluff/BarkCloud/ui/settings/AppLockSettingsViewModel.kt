package com.barkfluff.BarkCloud.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.AppLockManager
import com.barkfluff.BarkCloud.data.AppLockStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class AppLockSettingsUiState(val isEnabled: Boolean = false, val snackbar: String? = null)

class AppLockSettingsViewModel(
    private val store: AppLockStore,
    private val manager: AppLockManager,
) : ViewModel() {

    private val _state = MutableStateFlow(AppLockSettingsUiState(isEnabled = store.isEnabled))
    val state: StateFlow<AppLockSettingsUiState> = _state.asStateFlow()

    fun enable(pin: String) {
        store.enable(pin)
        _state.update { it.copy(isEnabled = true) }
    }

    fun disable() {
        store.disable()
        manager.unlock()
        _state.update { it.copy(isEnabled = false) }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return AppLockSettingsViewModel(app.appLockStore, app.appLockManager) as T
            }
        }
    }
}
