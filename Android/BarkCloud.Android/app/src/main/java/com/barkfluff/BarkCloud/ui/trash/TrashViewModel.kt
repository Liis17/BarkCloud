package com.barkfluff.BarkCloud.ui.trash

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import android.content.Context
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.TrashItem
import com.barkfluff.BarkCloud.ui.selection.BatchOutcome
import com.barkfluff.BarkCloud.ui.selection.runCloudBatch
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class TrashUiState(
    val isLoading: Boolean = true,
    val items: List<TrashItem> = emptyList(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val isRefreshing: Boolean = false,
    val isProcessing: Boolean = false,
    val selected: Set<String> = emptySet(),
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

class TrashViewModel(
    private val appContext: Context,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(TrashUiState())
    val state: StateFlow<TrashUiState> = _state.asStateFlow()

    private var cursorDeletedAt: Long? = null
    private var cursorEntryId: String = ""
    private var reloadJob: Job? = null

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    suspend fun reloadAndWait() {
        if (reloadJob?.isActive != true) reload()
        reloadJob?.join()
    }

    fun reload() {
        cursorDeletedAt = null
        cursorEntryId = ""
        reloadJob = viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val page = cloudRepository.listTrash(limit = 50)
                cursorDeletedAt = page.nextCursorDeletedAtMillis
                cursorEntryId = page.nextCursorEntryId
                _state.update {
                    it.copy(isLoading = false, isRefreshing = false, items = page.items, canLoadMore = page.hasMore)
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, isRefreshing = false, snackbar = e.message) }
            }
        }
    }

    fun loadMore() {
        val s = _state.value
        if (!s.canLoadMore || s.isLoadingMore || s.isLoading) return
        _state.update { it.copy(isLoadingMore = true) }
        viewModelScope.launch {
            try {
                val page = cloudRepository.listTrash(
                    limit = 50,
                    cursorDeletedAtMillis = cursorDeletedAt,
                    cursorEntryId = cursorEntryId,
                )
                cursorDeletedAt = page.nextCursorDeletedAtMillis
                cursorEntryId = page.nextCursorEntryId
                _state.update {
                    it.copy(isLoadingMore = false, items = it.items + page.items, canLoadMore = page.hasMore)
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun restore(entryId: String) = act(entryId) { cloudRepository.restoreFromTrash(entryId) }
    fun deleteForever(entryId: String) = act(entryId) { cloudRepository.deleteFromTrash(entryId) }

    private fun act(entryId: String, action: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                action()
                _state.update { it.copy(items = it.items.filterNot { item -> item.id == entryId }) }
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            }
        }
    }

    fun emptyTrash() {
        _state.update { it.copy(isProcessing = true) }
        viewModelScope.launch {
            try {
                cloudRepository.emptyTrash()
                _state.update { it.copy(isProcessing = false, items = emptyList()) }
            } catch (e: Exception) {
                _state.update { it.copy(isProcessing = false, snackbar = e.message) }
            }
        }
    }

    /** Выбор записей корзины (переживает пересоздание экрана). Во время операции не меняется. */
    fun setSelection(keys: Set<String>) = _state.update { if (it.isProcessing) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.isProcessing) it else it.copy(selected = emptySet()) }

    /** Восстанавливает выбранные; неудачные остаются выбранными. */
    fun restoreSelected() = runBatch {
        val outcome = runCloudBatch(_state.value.selected) { cloudRepository.restoreFromTrash(it) }
        applyOutcome(outcome)
    }

    /** Удаляет выбранные навсегда; вызывающий экран подтверждает действие заранее. */
    fun deleteSelectedForever() = runBatch {
        val outcome = runCloudBatch(_state.value.selected) { cloudRepository.deleteFromTrash(it) }
        applyOutcome(outcome)
    }

    private fun applyOutcome(outcome: BatchOutcome) {
        _state.update {
            it.copy(
                items = it.items.filterNot { item -> item.id in outcome.succeeded },
                selected = it.selected - outcome.succeeded,
                snackbar = if (outcome.failed > 0) appContext.getString(R.string.batch_failed_count, outcome.failed) else it.snackbar,
            )
        }
    }

    private fun runBatch(block: suspend () -> Unit) {
        if (_state.value.isProcessing) return
        _state.update { it.copy(isProcessing = true) }
        viewModelScope.launch {
            try {
                block()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            } finally {
                _state.update { it.copy(isProcessing = false) }
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
                return TrashViewModel(app, app.cloudRepository) as T
            }
        }
    }
}
