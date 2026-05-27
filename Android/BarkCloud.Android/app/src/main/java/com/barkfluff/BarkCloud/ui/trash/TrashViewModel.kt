package com.barkfluff.BarkCloud.ui.trash

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.TrashItem
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
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

class TrashViewModel(
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(TrashUiState())
    val state: StateFlow<TrashUiState> = _state.asStateFlow()

    private var cursorDeletedAt: Long? = null
    private var cursorEntryId: String = ""

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorDeletedAt = null
        cursorEntryId = ""
        viewModelScope.launch {
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

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return TrashViewModel(app.cloudRepository) as T
            }
        }
    }
}
