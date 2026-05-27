package com.barkfluff.BarkCloud.ui.albums

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class AlbumsUiState(
    val isLoading: Boolean = true,
    val albums: List<AlbumCard> = emptyList(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val isRefreshing: Boolean = false,
    val snackbar: String? = null,
)

class AlbumsViewModel(
    private val albumRepository: AlbumRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(AlbumsUiState())
    val state: StateFlow<AlbumsUiState> = _state.asStateFlow()

    private var cursorUpdatedAt: Long? = null
    private var cursorAlbumId: String = ""

    fun loadIfNeeded() {
        if (_state.value.albums.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorUpdatedAt = null
        cursorAlbumId = ""
        viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.albums.isNotEmpty(), isLoading = it.albums.isEmpty()) }
            try {
                val page = albumRepository.listAlbums(limit = 50)
                cursorUpdatedAt = page.nextCursorUpdatedAtMillis
                cursorAlbumId = page.nextCursorAlbumId
                _state.update {
                    it.copy(isLoading = false, isRefreshing = false, albums = page.albums, canLoadMore = page.hasMore)
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
                val page = albumRepository.listAlbums(
                    limit = 50,
                    cursorUpdatedAtMillis = cursorUpdatedAt,
                    cursorAlbumId = cursorAlbumId,
                )
                cursorUpdatedAt = page.nextCursorUpdatedAtMillis
                cursorAlbumId = page.nextCursorAlbumId
                _state.update {
                    it.copy(isLoadingMore = false, albums = it.albums + page.albums, canLoadMore = page.hasMore)
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun create(name: String) {
        if (name.isBlank()) return
        viewModelScope.launch {
            try {
                albumRepository.createAlbum(name.trim())
                reload()
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
                return AlbumsViewModel(app.albumRepository) as T
            }
        }
    }
}
