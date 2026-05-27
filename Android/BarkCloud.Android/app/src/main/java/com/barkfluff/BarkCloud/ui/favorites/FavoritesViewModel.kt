package com.barkfluff.BarkCloud.ui.favorites

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
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
    val isRefreshing: Boolean = false,
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

class FavoritesViewModel(
    private val cloudRepository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(FavoritesUiState())
    val state: StateFlow<FavoritesUiState> = _state.asStateFlow()

    private var cursorFavoritedAt: Long? = null
    private var cursorFileId: String = ""

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorFavoritedAt = null
        cursorFileId = ""
        viewModelScope.launch {
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
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun removeFavorite(fileId: String) {
        viewModelScope.launch {
            try {
                cloudRepository.removeFavorite(fileId)
                _state.update { it.copy(items = it.items.filterNot { a -> a.id == fileId }) }
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
                return FavoritesViewModel(app.cloudRepository) as T
            }
        }
    }
}
