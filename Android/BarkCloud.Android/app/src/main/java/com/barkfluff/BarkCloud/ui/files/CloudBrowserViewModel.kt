package com.barkfluff.BarkCloud.ui.files

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudDirectory
import com.barkfluff.BarkCloud.data.cloud.CloudFileEntry
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.PathCrumb
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.net.queryFileName
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class CloudBrowserUiState(
    val isLoading: Boolean = true,
    val crumbs: List<PathCrumb> = emptyList(),
    val subdirs: List<CloudDirectory> = emptyList(),
    val files: List<CloudFileEntry> = emptyList(),
    val isUploading: Boolean = false,
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && subdirs.isEmpty() && files.isEmpty()
}

class CloudBrowserViewModel(
    private val appContext: Context,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(CloudBrowserUiState())
    val state: StateFlow<CloudBrowserUiState> = _state.asStateFlow()

    private var directoryId: String = ""
    private var started = false

    fun start(directoryId: String) {
        if (started) return
        started = true
        this.directoryId = directoryId
        reload()
    }

    fun reload() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            try {
                val listing = cloudRepository.listDirectory(directoryId)
                val crumbs = if (directoryId.isEmpty()) emptyList()
                else runCatching { cloudRepository.path(directoryId) }.getOrDefault(emptyList())
                _state.update {
                    it.copy(
                        isLoading = false,
                        subdirs = listing.subdirs,
                        files = listing.files,
                        crumbs = crumbs,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, snackbar = e.message) }
            }
        }
    }

    fun createFolder(name: String) = mutate { cloudRepository.createDirectory(directoryId, name.trim()) }
    fun renameDirectory(id: String, newName: String) = mutate { cloudRepository.renameDirectory(id, newName.trim()) }
    fun deleteDirectory(id: String) = mutate { cloudRepository.deleteDirectory(id) }
    fun renameFile(entryId: String, newName: String) = mutate { cloudRepository.renameFileEntry(entryId, newName.trim()) }
    fun deleteFile(entryId: String) = mutate { cloudRepository.deleteFileEntry(entryId) }
    fun moveDirectory(id: String, target: String) = mutate { cloudRepository.moveDirectory(id, target) }
    fun moveFile(entryId: String, target: String) = mutate { cloudRepository.moveFileEntry(entryId, target) }

    fun upload(uris: List<Uri>) {
        if (uris.isEmpty()) return
        _state.update { it.copy(isUploading = true) }
        viewModelScope.launch {
            var failures = 0
            uris.forEach { uri ->
                runCatching {
                    (appContext as BarkCloudApplication).uploadQueue.enqueue(uri, queryFileName(appContext, uri), directoryId)
                }
                    .onFailure { failures++ }
            }
            if (failures < uris.size) UploadScheduler.enqueue(appContext)
            _state.update { it.copy(isUploading = false, snackbar = if (failures == 0) UPLOAD_QUEUED else UPLOAD_PARTIAL) }
            reload()
        }
    }

    private fun mutate(action: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                action()
                reload()
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            }
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        private const val UPLOAD_PARTIAL = "Часть файлов не загрузилась"
        private const val UPLOAD_QUEUED = "Загрузка поставлена в очередь"

        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return CloudBrowserViewModel(app, app.cloudRepository) as T
            }
        }
    }
}
