package com.barkfluff.BarkCloud.ui.settings

import android.content.Context
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.widgets.StorageWidgetBridge
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ProfileUiState(
    val isLoading: Boolean = true,
    val usedStorage: Long = 0,
    val storageLimit: Long = 0,
    val snackbar: String? = null,
)

class ProfileViewModel(
    private val appContext: Context,
    private val transfer: FileTransferService,
) : ViewModel() {

    private val _state = MutableStateFlow(ProfileUiState())
    val state: StateFlow<ProfileUiState> = _state.asStateFlow()

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val storage = transfer.storageInfo()
                StorageWidgetBridge.update(appContext, storage.used, storage.limit)
                _state.update {
                    it.copy(
                        isLoading = false,
                        usedStorage = storage.used,
                        storageLimit = storage.limit,
                    )
                }
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
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
                return ProfileViewModel(app, app.fileTransfer) as T
            }
        }
    }
}
