package com.barkfluff.BarkCloud.ui.vault

import android.content.Context
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
import com.barkfluff.BarkCloud.ui.selection.BatchOutcome
import com.barkfluff.BarkCloud.ui.selection.runCloudBatch
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
 * Per-session блокировка. Каждая сессия (поколение) получает номер; [relock] увеличивает его, и всё,
 * что стартовало в прошлом поколении, больше не может ни поднять [VaultLockState.UNLOCKED], ни запустить
 * следующий элемент пакета. Невыполненные ID остаются в [retainedIds] (не в состоянии экрана) и
 * возвращаются в выбор только после новой успешной разблокировки.
 */
class VaultViewModel(
    private val appContext: Context,
    private val store: VaultStore,
    private val albumRepository: AlbumRepository,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(VaultUiState())
    val state: StateFlow<VaultUiState> = _state.asStateFlow()

    private var sessionGeneration = 0L
    private var retainedIds: Set<String> = emptySet()

    fun unlock(activity: FragmentActivity, title: String) {
        val generation = onUnlockStarted() ?: return
        viewModelScope.launch {
            val ok = BiometricGate.authenticate(activity, title)
            onUnlockResult(generation, ok)
        }
    }

    /** Начало разблокировки; возвращает поколение или null, если разблокировка уже идёт или открыто. */
    internal fun onUnlockStarted(): Long? {
        if (_state.value.lockState != VaultLockState.LOCKED) return null
        _state.update { it.copy(lockState = VaultLockState.UNLOCKING) }
        return sessionGeneration
    }

    /**
     * Итог биометрии. Результат старого поколения (после блокировки) не открывает Vault: состояние не
     * меняется, а после успешной новой разблокировки отложенные ID возвращаются в выбор для повтора.
     */
    internal fun onUnlockResult(generation: Long, ok: Boolean) {
        if (generation != sessionGeneration) return
        if (!ok) {
            _state.update { if (it.lockState == VaultLockState.UNLOCKING) it.copy(lockState = VaultLockState.LOCKED) else it }
            return
        }
        val restored = retainedIds
        retainedIds = emptySet()
        _state.update {
            it.copy(
                lockState = VaultLockState.UNLOCKED,
                items = store.items(),
                selected = restored.intersect(store.items().map { item -> item.fileId }.toSet()),
            )
        }
    }

    fun relock() {
        sessionGeneration++
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

    /**
     * Удаление в облако по одному элементу. Перед каждым элементом проверяется, что поколение сессии
     * не сменилось и vault открыт; иначе пакет останавливается, а нерассмотренные ID сохраняются для повтора.
     */
    fun deleteSelected() {
        val generation = sessionGeneration
        runBatch {
            val outcome = runCloudBatch(
                _state.value.selected,
                shouldContinue = { generation == sessionGeneration && _state.value.lockState == VaultLockState.UNLOCKED },
            ) { fileId ->
                cloudRepository.deleteUserMedia(fileId)
                store.remove(fileId) // только после подтверждённого удаления
            }
            applyDeleteOutcome(outcome, generation)
        }
    }

    private fun applyDeleteOutcome(outcome: BatchOutcome, generation: Long) {
        // Результаты уже выполненных удалений применяются всегда, даже после блокировки.
        val stillOpen = generation == sessionGeneration
        retainedIds = outcome.remaining
        _state.update {
            it.copy(
                items = it.items.filterNot { item -> item.fileId in outcome.succeeded },
                selected = if (stillOpen) outcome.remaining else emptySet(),
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
