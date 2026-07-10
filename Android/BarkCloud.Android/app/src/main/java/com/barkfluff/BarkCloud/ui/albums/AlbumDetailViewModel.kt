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
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.net.queryFileName
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
    val isRefreshing: Boolean = false,
    val isUploading: Boolean = false,
    val snackbar: String? = null,
)

class AlbumDetailViewModel(
    private val appContext: Context,
    private val albumRepository: AlbumRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(AlbumDetailUiState())
    val state: StateFlow<AlbumDetailUiState> = _state.asStateFlow()

    private val _deleted = Channel<Unit>(Channel.BUFFERED)
    val deleted = _deleted.receiveAsFlow()

    private var albumId: String = ""
    private var cursorAddedAt: Long? = null
    private var cursorFileId: String = ""
    private var started = false

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
        viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val page = albumRepository.listItems(albumId, limit = 60)
                cursorAddedAt = page.nextCursorAddedAtMillis
                cursorFileId = page.nextCursorFileId
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
                val page = albumRepository.listItems(
                    albumId, limit = 60,
                    cursorAddedAtMillis = cursorAddedAt, cursorFileId = cursorFileId,
                )
                cursorAddedAt = page.nextCursorAddedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(isLoadingMore = false, items = it.items + page.items, canLoadMore = page.hasMore)
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

    fun setCover(fileId: String) {
        viewModelScope.launch {
            runCatching { albumRepository.updateAlbum(albumId, coverFileId = fileId) }
                .onFailure { e -> _state.update { it.copy(snackbar = e.message) } }
        }
    }

    fun removeItem(fileId: String) {
        viewModelScope.launch {
            try {
                albumRepository.removeItems(albumId, listOf(fileId))
                _state.update { it.copy(items = it.items.filterNot { a -> a.id == fileId }) }
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            }
        }
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
                return AlbumDetailViewModel(app, app.albumRepository) as T
            }
        }
    }
}
