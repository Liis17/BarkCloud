package com.barkfluff.BarkCloud.ui.shared

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.PublicShareItem
import com.barkfluff.BarkCloud.data.cloud.PublicShareKind
import com.barkfluff.BarkCloud.data.cloud.SharedRepository
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class MySharesUiState(
    val isLoading: Boolean = true,
    val items: List<PublicShareItem> = emptyList(),
    val isRefreshing: Boolean = false,
    val snackbar: String? = null,
) {
    val isEmpty: Boolean get() = !isLoading && items.isEmpty()
}

/**
 * Таб «Мои публичные»: сводит ссылки на файлы/папки/альбомы в один список.
 * Без пагинации (лимит 200 на каждый тип — потолок бэкенда, хватает для управления).
 * Файлы обязательны, папки/альбомы — best-effort (ошибка одного типа не рушит список).
 */
class MySharesViewModel(
    private val repo: SharedRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(MySharesUiState())
    val state: StateFlow<MySharesUiState> = _state.asStateFlow()

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val merged = coroutineScope {
                    val files = async { repo.listMyShares() }
                    val folders = async { runCatching { repo.listMyFolderShares() }.getOrDefault(emptyList()) }
                    val albums = async { runCatching { repo.listMyAlbumShares() }.getOrDefault(emptyList()) }
                    (files.await() + folders.await() + albums.await()).sortedByDescending { it.createdAtMillis }
                }
                _state.update { it.copy(isLoading = false, isRefreshing = false, items = merged) }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, isRefreshing = false, snackbar = e.message) }
            }
        }
    }

    /** Оптимистичное удаление из списка, откат при ошибке — revoke идемпотентен на бэкенде. */
    fun revoke(item: PublicShareItem) {
        _state.update { it.copy(items = it.items.filterNot { i -> i.id == item.id }) }
        viewModelScope.launch {
            try {
                when (item.kind) {
                    PublicShareKind.FILE -> repo.revokeShare(item.recordId)
                    PublicShareKind.FOLDER -> repo.revokeFolderShare(item.recordId)
                    PublicShareKind.ALBUM -> repo.revokeAlbumShare(item.recordId)
                }
            } catch (e: Exception) {
                _state.update {
                    it.copy(
                        items = (it.items + item).sortedByDescending { i -> i.createdAtMillis },
                        snackbar = e.message,
                    )
                }
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
                return MySharesViewModel(app.sharedRepository) as T
            }
        }
    }
}
