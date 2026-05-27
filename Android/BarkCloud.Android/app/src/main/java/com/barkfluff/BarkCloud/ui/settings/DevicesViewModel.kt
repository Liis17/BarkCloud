package com.barkfluff.BarkCloud.ui.settings

import barkcloud.users.UsersApiOuterClass.Device
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

data class DevicesUiState(
    val isLoading: Boolean = true,
    val devices: List<Device> = emptyList(),
    val currentDeviceId: String = "",
    val snackbar: String? = null,
)

class DevicesViewModel(
    private val userRepository: UserRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(DevicesUiState())
    val state: StateFlow<DevicesUiState> = _state.asStateFlow()

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val devices = userRepository.getDevices()
                val current = runCatching { userRepository.getCurrentDevice() }.getOrNull()
                _state.update {
                    it.copy(
                        isLoading = false,
                        devices = devices,
                        currentDeviceId = current?.deviceId.orEmpty(),
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun rename(deviceId: String, customName: String) {
        viewModelScope.launch {
            try {
                userRepository.renameDevice(deviceId, customName)
                load()
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            }
        }
    }

    fun delete(deviceId: String) {
        viewModelScope.launch {
            try {
                userRepository.deleteDevice(deviceId)
                load()
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
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
                return DevicesViewModel(app.userRepository) as T
            }
        }
    }
}
