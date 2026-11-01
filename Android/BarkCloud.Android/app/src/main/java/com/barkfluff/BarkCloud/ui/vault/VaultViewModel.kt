package com.barkfluff.BarkCloud.ui.vault

import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.BiometricGate
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.vault.VaultItem
import com.barkfluff.BarkCloud.data.vault.VaultStore
import com.barkfluff.BarkCloud.ui.selection.runCloudBatch
import android.content.Context
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

enum class VaultLockState { LOCKED, UNLOCKING, UNLOCKED }

data class VaultUiState(
    val lockState: VaultLockState = VaultLockState.LOCKED,
    val items: List<VaultItem> = emptyList(),
    val selected: Set<String> = emptySet(),
    val busy: Boolean = false,
    val snackbar: String? = null,
)

/**
 * Per-session блокировка (без grace-period — проще, чем App Lock): релочится
 * при каждом уходе экрана из foreground ([relock], вызывается из [VaultScreen]
 * на ON_STOP), зеркалит iOS scenePhase-поведение VaultViewModel.
 *
 * При блокировке выбор сбрасывается и новые пакетные операции не запускаются; уже идущая
 * операция дописывает результат в состояние (VaultStore согласуется с облаком).
 */
class VaultViewModel(
    private val appContext: Context,
    private val store: VaultStore,
    private val albumRepository: AlbumRepository,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

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
        _state.update { it.copy(lockState = VaultLockState.LOCKED, selected = emptySet()) }
    }

    fun setSelection(keys: Set<String>) = _state.update { if (it.busy) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.busy) it else it.copy(selected = emptySet()) }

    /** Убирает выбранные из хранилища vault; файлы в облаке остаются. */
    fun removeSelectedFromVault() = runBatch {
        val ids = _state.value.selected
        ids.forEach { store.remove(it) }
        _state.update { it.copy(items = it.items.filterNot { item -> item.fileId in ids }, selected = emptySet()) }
    }

    fun addSelectedToAlbum(albumId: String) = runBatch {
        albumRepository.addItems(albumId, _state.value.selected.toList())
        _state.update { it.copy(selected = emptySet()) }
    }

    /** Файлы уходят в корзину по одному; неудачные остаются выбранными для повтора. */
    fun deleteSelected() = runBatch {
        val outcome = runCloudBatch(_state.value.selected) { cloudRepository.deleteUserMedia(it) }
        outcome.succeeded.forEach { store.remove(it) }
        _state.update {
            it.copy(
                items = it.items.filterNot { item -> item.fileId in outcome.succeeded },
                selected = it.selected - outcome.succeeded,
                snackbar = if (outcome.failed > 0) appContext.getString(R.string.batch_failed_count, outcome.failed) else it.snackbar,
            )
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    suspend fun deleteFile(fileId: String) {
        cloudRepository.deleteUserMedia(fileId)
        store.remove(fileId)
        _state.update { it.copy(items = it.items.filterNot { item -> item.fileId == fileId }) }
    }

    private fun runBatch(block: suspend () -> Unit) {
        if (_state.value.busy || _state.value.lockState != VaultLockState.UNLOCKED) return
        _state.update { it.copy(busy = true) }
        viewModelScope.launch {
            try {
                block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            } finally {
                _state.update { it.copy(busy = false) }
            }
        }
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return VaultViewModel(app, app.vaultStore, app.albumRepository, app.cloudRepository) as T
            }
        }
    }
}
