package com.barkfluff.BarkCloud.ui.smartfolders

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class SmartFolderDetailUiState(
    val isLoading: Boolean = true,
    val isRefreshing: Boolean = false,
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val items: List<MediaAsset> = emptyList(),
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

class SmartFolderDetailViewModel(
    private val repository: DynamicFolderRepository,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(SmartFolderDetailUiState())
    val state: StateFlow<SmartFolderDetailUiState> = _state.asStateFlow()

    private var folderId = ""
    private var started = false
    private var cursorCreatedAt: Long? = null
    private var cursorFileId = ""
    private var reloadJob: Job? = null

    fun start(folderId: String) {
        if (started) return
        started = true
        this.folderId = folderId
        reload()
    }

    fun reload() {
        cursorCreatedAt = null
        cursorFileId = ""
        reloadJob = viewModelScope.launch {
            _state.update { it.copy(isLoading = it.items.isEmpty(), isRefreshing = it.items.isNotEmpty()) }
            runCatching { repository.listItems(folderId) }
                .onSuccess { page ->
                    cursorCreatedAt = page.nextCursorCreatedAtMillis
                    cursorFileId = page.nextCursorFileId
                    _state.update {
                        it.copy(
                            isLoading = false,
                            isRefreshing = false,
                            items = page.items,
                            canLoadMore = page.hasMore,
                        )
                    }
                }
                .onFailure { error ->
                    _state.update {
                        it.copy(isLoading = false, isRefreshing = false, snackbar = error.message)
                    }
                }
        }
    }

    suspend fun reloadAndWait() {
        if (reloadJob?.isActive != true) reload()
        reloadJob?.join()
    }

    suspend fun deleteFile(fileId: String) {
        cloudRepository.deleteUserMedia(fileId)
        _state.update { it.copy(items = it.items.filterNot { asset -> asset.id == fileId }) }
    }

    fun loadMore() {
        val s = _state.value
        if (!s.canLoadMore || s.isLoading || s.isLoadingMore) return
        _state.update { it.copy(isLoadingMore = true) }
        viewModelScope.launch {
            runCatching {
                repository.listItems(
                    folderId = folderId,
                    cursorCreatedAtMillis = cursorCreatedAt,
                    cursorFileId = cursorFileId,
                )
            }
                .onSuccess { page ->
                    cursorCreatedAt = page.nextCursorCreatedAtMillis
                    cursorFileId = page.nextCursorFileId
                    _state.update {
                        it.copy(
                            isLoadingMore = false,
                            items = it.items + page.items,
                            canLoadMore = page.hasMore,
                        )
                    }
                }
                .onFailure { error ->
                    _state.update { it.copy(isLoadingMore = false, snackbar = error.message) }
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
                return SmartFolderDetailViewModel(app.dynamicFolderRepository, app.cloudRepository) as T
            }
        }
    }
}
