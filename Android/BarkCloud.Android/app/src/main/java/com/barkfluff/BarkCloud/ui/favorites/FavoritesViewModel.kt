package com.barkfluff.BarkCloud.ui.favorites

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import android.content.Context
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.ui.selection.runCloudBatch
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class FavoritesUiState(
    val isLoading: Boolean = true,
    val items: List<MediaAsset> = emptyList(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val pagesCompleted: Int = 0,
    val isRefreshing: Boolean = false,
    val snackbar: String? = null,
    val selected: Set<String> = emptySet(),
    val busy: Boolean = false,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

class FavoritesViewModel(
    private val appContext: Context,
    private val albumRepository: AlbumRepository,
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(FavoritesUiState())
    val state: StateFlow<FavoritesUiState> = _state.asStateFlow()

    private var cursorFavoritedAt: Long? = null
    private var cursorFileId: String = ""
    private var reloadJob: Job? = null

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    suspend fun reloadAndWait() {
        if (reloadJob?.isActive != true) reload()
        reloadJob?.join()
    }

    fun reload() {
        cursorFavoritedAt = null
        cursorFileId = ""
        reloadJob = viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val page = cloudRepository.listFavorites(limit = 60)
                cursorFavoritedAt = page.nextCursorFavoritedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(
                        isLoading = false,
                        isRefreshing = false,
                        items = page.items.map { f -> f.asset },
                        pagesCompleted = it.pagesCompleted + 1,
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
                val page = cloudRepository.listFavorites(
                    limit = 60,
                    cursorFavoritedAtMillis = cursorFavoritedAt,
                    cursorFileId = cursorFileId,
                )
                cursorFavoritedAt = page.nextCursorFavoritedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(
                        isLoadingMore = false,
                        items = it.items + page.items.map { f -> f.asset },
                        pagesCompleted = it.pagesCompleted + 1,
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    /** Выбор избранного (переживает пересоздание экрана). Во время операции не меняется. */
    fun setSelection(keys: Set<String>) = _state.update { if (it.busy) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.busy) it else it.copy(selected = emptySet()) }

    /** Снимает выбранное с избранного; неудачные остаются выбранными. */
    fun removeSelectedFavorites() = runBatch {
        val outcome = runCloudBatch(_state.value.selected) { cloudRepository.removeFavorite(it) }
        _state.update {
            it.copy(
                items = it.items.filterNot { asset -> asset.id in outcome.succeeded },
                selected = it.selected - outcome.succeeded,
                snackbar = if (outcome.failed > 0) appContext.getString(R.string.batch_failed_count, outcome.failed) else it.snackbar,
            )
        }
    }

    /** Добавляет выбранные в альбом одним запросом. */
    fun addSelectedToAlbum(albumId: String) = runBatch {
        albumRepository.addItems(albumId, _state.value.selected.toList())
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

    suspend fun deleteFile(fileId: String) {
        cloudRepository.deleteUserMedia(fileId)
        _state.update { it.copy(items = it.items.filterNot { asset -> asset.id == fileId }) }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return FavoritesViewModel(app, app.albumRepository, app.cloudRepository) as T
            }
        }
    }
}
