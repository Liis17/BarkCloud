package com.barkfluff.BarkCloud.ui.shared

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.SharedFolderEntry
import com.barkfluff.BarkCloud.data.cloud.SharedRepository
import com.barkfluff.BarkCloud.data.cloud.SharedWithMeEntry
import com.barkfluff.BarkCloud.data.users.UserRepository
import com.barkfluff.BarkCloud.net.FileTransferService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File

data class SharedWithMeUiState(
    val isLoading: Boolean = true,
    val items: List<SharedWithMeEntry> = emptyList(),
    val folders: List<SharedFolderEntry> = emptyList(),
    val owners: Map<Long, User> = emptyMap(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val isRefreshing: Boolean = false,
    val downloadingFileId: String? = null,
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty() && folders.isEmpty()
}

/**
 * Таб «Мне доступны»: входящие файловые гранты — cursor-paginated
 * ([SharedRepository.listSharedWithMe]), папочные — best-effort без курсора.
 * Нет revoke — отправитель управляет доступом.
 */
class SharedWithMeViewModel(
    private val repo: SharedRepository,
    private val users: UserRepository,
    private val transfer: FileTransferService,
) : ViewModel() {

    private val _state = MutableStateFlow(SharedWithMeUiState())
    val state: StateFlow<SharedWithMeUiState> = _state.asStateFlow()

    private var cursorSharedAt: Long? = null
    private var cursorGrantId: String = ""

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.folders.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorSharedAt = null
        cursorGrantId = ""
        viewModelScope.launch {
            _state.update {
                it.copy(
                    isRefreshing = it.items.isNotEmpty() || it.folders.isNotEmpty(),
                    isLoading = it.items.isEmpty() && it.folders.isEmpty(),
                )
            }
            try {
                val page = repo.listSharedWithMe(limit = 60)
                cursorSharedAt = page.nextCursorSharedAtMillis
                cursorGrantId = page.nextCursorGrantId
                val folders = runCatching { repo.listSharedFoldersWithMe() }.getOrDefault(emptyList())
                val ids = (page.items.map { it.ownerUserId } + folders.map { it.ownerUserId }).distinct()
                val resolved = runCatching { users.listByIds(ids) }.getOrDefault(emptyList()).associateBy { it.id }
                _state.update {
                    it.copy(
                        isLoading = false,
                        isRefreshing = false,
                        items = page.items,
                        folders = folders,
                        owners = it.owners + resolved,
                        canLoadMore = page.hasMore,
                    )
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
                val page = repo.listSharedWithMe(
                    limit = 60,
                    cursorSharedAtMillis = cursorSharedAt,
                    cursorGrantId = cursorGrantId,
                )
                cursorSharedAt = page.nextCursorSharedAtMillis
                cursorGrantId = page.nextCursorGrantId
                val newIds = page.items.map { it.ownerUserId }.distinct().filterNot { _state.value.owners.containsKey(it) }
                val resolved = runCatching { users.listByIds(newIds) }.getOrDefault(emptyList()).associateBy { it.id }
                _state.update {
                    it.copy(
                        isLoadingMore = false,
                        items = it.items + page.items,
                        owners = it.owners + resolved,
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    /** Скачивает файл во временный кэш и возвращает его для открытия/шеринга вызывающей стороной. */
    fun download(entry: SharedWithMeEntry, onDownloaded: (File) -> Unit) {
        _state.update { it.copy(downloadingFileId = entry.asset.id) }
        viewModelScope.launch {
            try {
                val url = repo.getSharedFileDownloadUrl(entry.asset.id)
                val file = transfer.download(url, entry.asset.fileName)
                _state.update { it.copy(downloadingFileId = null) }
                onDownloaded(file)
            } catch (e: Exception) {
                _state.update { it.copy(downloadingFileId = null, snackbar = e.message) }
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
                return SharedWithMeViewModel(app.sharedRepository, app.userRepository, app.fileTransfer) as T
            }
        }
    }
}
