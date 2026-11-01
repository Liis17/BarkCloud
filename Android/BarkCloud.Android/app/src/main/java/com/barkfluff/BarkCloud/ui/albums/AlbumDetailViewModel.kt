package com.barkfluff.BarkCloud.ui.albums

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.ui.selection.runCloudBatch
import com.barkfluff.BarkCloud.net.queryFileName
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class AlbumDetailUiState(
    val name: String = "",
    val isLoading: Boolean = true,
    val items: List<MediaAsset> = emptyList(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val pagesCompleted: Int = 0,
    val isRefreshing: Boolean = false,
    val isUploading: Boolean = false,
    val selected: Set<String> = emptySet(),
    val busy: Boolean = false,
    val snackbar: String? = null,
)

class AlbumDetailViewModel(
    private val appContext: Context,
    private val albumRepository: AlbumRepository,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(AlbumDetailUiState())
    val state: StateFlow<AlbumDetailUiState> = _state.asStateFlow()

    private val _deleted = Channel<Unit>(Channel.BUFFERED)
    val deleted = _deleted.receiveAsFlow()

    private var albumId: String = ""
    private var cursorAddedAt: Long? = null
    private var cursorFileId: String = ""
    private var started = false
    private var reloadJob: Job? = null

    fun start(albumId: String, name: String) {
        if (started) return
        started = true
        this.albumId = albumId
        _state.update { it.copy(name = name) }
        reload()
    }

    fun reload() {
        cursorAddedAt = null
        cursorFileId = ""
        reloadJob = viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val page = albumRepository.listItems(albumId, limit = 60)
                cursorAddedAt = page.nextCursorAddedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(isLoading = false, isRefreshing = false, items = page.items, canLoadMore = page.hasMore, pagesCompleted = it.pagesCompleted + 1)
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, isRefreshing = false, snackbar = e.message) }
            }
        }
    }

    suspend fun reloadAndWait() {
        if (reloadJob?.isActive != true) reload()
        reloadJob?.join()
    }

    fun loadMore() {
        val s = _state.value
        if (!s.canLoadMore || s.isLoadingMore || s.isLoading) return
        _state.update { it.copy(isLoadingMore = true) }
        viewModelScope.launch {
            try {
                val page = albumRepository.listItems(
                    albumId, limit = 60,
                    cursorAddedAtMillis = cursorAddedAt, cursorFileId = cursorFileId,
                )
                cursorAddedAt = page.nextCursorAddedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(isLoadingMore = false, items = it.items + page.items, canLoadMore = page.hasMore, pagesCompleted = it.pagesCompleted + 1)
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun uploadAndAdd(uris: List<Uri>) {
        if (uris.isEmpty()) return
        _state.update { it.copy(isUploading = true) }
        viewModelScope.launch {
            try {
                val app = appContext as BarkCloudApplication
                uris.forEach { uri ->
                    app.uploadQueue.enqueue(
                        uri,
                        queryFileName(appContext, uri),
                        source = com.barkfluff.BarkCloud.data.upload.UploadSource.ALBUM,
                        albumId = albumId,
                    )
                }
                UploadScheduler.enqueue(appContext)
                _state.update {
                    it.copy(
                        isUploading = false,
                        snackbar = appContext.getString(R.string.share_queued),
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isUploading = false, snackbar = e.message) }
            }
        }
    }

    /** Выбор элементов альбома (переживает пересоздание экрана). Во время операции не меняется. */
    fun setSelection(keys: Set<String>) = _state.update { if (it.busy) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.busy) it else it.copy(selected = emptySet()) }

    /** Убирает выбранные из этого альбома одним запросом; файлы остаются в облаке. */
    fun removeSelectedFromAlbum() = runBatch {
        val ids = _state.value.selected.toList()
        albumRepository.removeItems(albumId, ids)
        _state.update { it.copy(items = it.items.filterNot { asset -> asset.id in ids }, selected = emptySet()) }
    }

    /** Добавляет выбранные в другой альбом. */
    fun addSelectedToAlbum(targetAlbumId: String) = runBatch {
        albumRepository.addItems(targetAlbumId, _state.value.selected.toList())
        _state.update { it.copy(selected = emptySet()) }
    }

    /** Файлы уходят в корзину по одному; неудачные остаются выбранными для повтора. */
    fun deleteSelected() = runBatch {
        val outcome = runCloudBatch(_state.value.selected) { cloudRepository.deleteUserMedia(it) }
        _state.update {
            it.copy(
                items = it.items.filterNot { asset -> asset.id in outcome.succeeded },
                selected = it.selected - outcome.succeeded,
                snackbar = if (outcome.failed > 0) appContext.getString(R.string.batch_failed_count, outcome.failed) else it.snackbar,
            )
        }
    }

    /** Обложка только для одного выбранного элемента. */
    fun setCoverFromSelection() {
        val fileId = _state.value.selected.singleOrNull() ?: return
        setCover(fileId)
        clearSelection()
    }

    private fun runBatch(block: suspend () -> Unit) {
        if (_state.value.busy) return
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

    fun setCover(fileId: String) {
        viewModelScope.launch {
            runCatching { albumRepository.updateAlbum(albumId, coverFileId = fileId) }
                .onFailure { e -> _state.update { it.copy(snackbar = e.message) } }
        }
    }

    suspend fun deleteFile(fileId: String) {
        cloudRepository.deleteUserMedia(fileId)
        _state.update { it.copy(items = it.items.filterNot { asset -> asset.id == fileId }) }
    }

    fun rename(name: String) {
        if (name.isBlank()) return
        viewModelScope.launch {
            try {
                albumRepository.updateAlbum(albumId, name = name.trim())
                _state.update { it.copy(name = name.trim()) }
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            }
        }
    }

    fun delete() {
        viewModelScope.launch {
            try {
                albumRepository.deleteAlbum(albumId)
                _deleted.send(Unit)
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
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
                return AlbumDetailViewModel(app, app.albumRepository, app.cloudRepository) as T
            }
        }
    }
}
