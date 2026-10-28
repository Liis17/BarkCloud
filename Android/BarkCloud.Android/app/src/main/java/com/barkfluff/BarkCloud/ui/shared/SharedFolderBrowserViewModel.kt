package com.barkfluff.BarkCloud.ui.shared

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.cloud.PublicDir
import com.barkfluff.BarkCloud.data.cloud.PublicFile
import com.barkfluff.BarkCloud.data.cloud.SharedRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File

data class SharedFolderUiState(
    val isLoading: Boolean = true,
    val found: Boolean = true,
    val name: String = "",
    val subdirs: List<PublicDir> = emptyList(),
    val files: List<PublicFile> = emptyList(),
    val downloadingFileId: String? = null,
    val snackbar: String? = null,
)

/**
 * Read-only навигация по доступной мне чужой папке (`ListSharedDirectory`) — без
 * rename/move/delete/create, зеркалит iOS SharedFolderBrowserScreen. Файлы скачиваются
 * напрямую по `download_url` из ответа — доп. RPC не нужен.
 */
class SharedFolderBrowserViewModel(
    private val repo: SharedRepository,
    private val fileCache: FileCacheService,
) : ViewModel() {

    private val _state = MutableStateFlow(SharedFolderUiState())
    val state: StateFlow<SharedFolderUiState> = _state.asStateFlow()

    private var directoryId = ""
    private var reloadJob: Job? = null

    fun start(directoryId: String) {
        this.directoryId = directoryId
        reload()
    }

    suspend fun reloadAndWait() {
        if (reloadJob?.isActive != true) reload()
        reloadJob?.join()
    }

    fun reload() {
        reloadJob = viewModelScope.launch {
            _state.update { it.copy(isLoading = it.subdirs.isEmpty() && it.files.isEmpty()) }
            try {
                val listing = repo.listSharedDirectory(directoryId)
                _state.update {
                    it.copy(
                        isLoading = false,
                        found = listing.found,
                        name = listing.name,
                        subdirs = listing.subdirs,
                        files = listing.files,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun download(file: PublicFile, onDownloaded: suspend (File) -> Unit) {
        _state.update { it.copy(downloadingFileId = file.id) }
        viewModelScope.launch {
            try {
                val downloaded = fileCache.loadSharedOriginal(file.id, file.name, file.downloadUrl)
                _state.update { it.copy(downloadingFileId = null) }
                onDownloaded(downloaded)
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
                return SharedFolderBrowserViewModel(app.sharedRepository, app.fileCache) as T
            }
        }
    }
}
