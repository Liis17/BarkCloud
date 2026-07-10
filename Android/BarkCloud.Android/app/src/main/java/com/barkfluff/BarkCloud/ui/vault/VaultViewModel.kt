package com.barkfluff.BarkCloud.ui.vault

import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.BiometricGate
import com.barkfluff.BarkCloud.data.vault.VaultItem
import com.barkfluff.BarkCloud.data.vault.VaultStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

enum class VaultLockState { LOCKED, UNLOCKING, UNLOCKED }

data class VaultUiState(
    val lockState: VaultLockState = VaultLockState.LOCKED,
    val items: List<VaultItem> = emptyList(),
)

/**
 * Per-session блокировка (без grace-period — проще, чем App Lock): релочится
 * при каждом уходе экрана из foreground ([relock], вызывается из [VaultScreen]
 * на ON_STOP), зеркалит iOS scenePhase-поведение VaultViewModel.
 */
class VaultViewModel(private val store: VaultStore) : ViewModel() {

    private val _state = MutableStateFlow(VaultUiState())
    val state: StateFlow<VaultUiState> = _state.asStateFlow()

    fun unlock(activity: FragmentActivity, title: String) {
        if (_state.value.lockState != VaultLockState.LOCKED) return
        _state.update { it.copy(lockState = VaultLockState.UNLOCKING) }
        viewModelScope.launch {
            val ok = BiometricGate.authenticate(activity, title)
            _state.update {
                if (ok) it.copy(lockState = VaultLockState.UNLOCKED, items = store.items())
                else it.copy(lockState = VaultLockState.LOCKED)
            }
        }
    }

    fun relock() {
        _state.update { it.copy(lockState = VaultLockState.LOCKED) }
    }

    fun remove(fileId: String) {
        store.remove(fileId)
        _state.update { it.copy(items = it.items.filterNot { item -> item.fileId == fileId }) }
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return VaultViewModel(app.vaultStore) as T
            }
        }
    }
}
