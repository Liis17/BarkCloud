package com.barkfluff.BarkCloud.ui.media

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.data.vault.VaultItem
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.net.queryFileName
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

data class MediaGridUiState(
    val isLoading: Boolean = true,
    val items: List<MediaAsset> = emptyList(),
    val isLoadingMore: Boolean = false,
    val canLoadMore: Boolean = true,
    val isRefreshing: Boolean = false,
    val isUploading: Boolean = false,
    val snackbar: String? = null,
)

class MediaGridViewModel(
    private val appContext: Context,
    private val cloudRepository: CloudRepository,
    private val kind: CloudMediaKind,
) : ViewModel() {

    private val _state = MutableStateFlow(MediaGridUiState())
    val state: StateFlow<MediaGridUiState> = _state.asStateFlow()

    private var cursorCreatedAt: Long? = null
    private var cursorFileId: String = ""
    private val observedCompleted = mutableSetOf<String>()

    init {
        val app = appContext as BarkCloudApplication
        viewModelScope.launch {
            app.uploadQueue.recentJobs.collectLatest { jobs ->
                if (jobs.any { it.phase == UploadPhase.COMPLETED && observedCompleted.add(it.id) }) reload()
            }
        }
    }

    fun loadIfNeeded() {
        if (_state.value.items.isEmpty() && _state.value.isLoading) reload()
    }

    fun reload() {
        cursorCreatedAt = null
        cursorFileId = ""
        viewModelScope.launch {
            _state.update { it.copy(isRefreshing = it.items.isNotEmpty(), isLoading = it.items.isEmpty()) }
            try {
                val page = cloudRepository.listUserMedia(kind, limit = 60)
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
                val page = cloudRepository.listUserMedia(
                    kind,
                    limit = 60,
                    cursorCreatedAtMillis = cursorCreatedAt,
                    cursorFileId = cursorFileId,
                )
                cursorCreatedAt = page.nextCursorCreatedAtMillis
                cursorFileId = page.nextCursorFileId
                _state.update {
                    it.copy(
                        isLoadingMore = false,
                        items = it.items + page.items,
                        canLoadMore = page.hasMore,
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoadingMore = false, snackbar = e.message) }
            }
        }
    }

    fun upload(uris: List<Uri>) {
        if (uris.isEmpty()) return
        _state.update { it.copy(isUploading = true) }
        viewModelScope.launch {
            var failures = 0
            uris.forEach { uri ->
                runCatching {
                    (appContext as BarkCloudApplication).uploadQueue.enqueue(
                        uri,
                        queryFileName(appContext, uri),
                        source = com.barkfluff.BarkCloud.data.upload.UploadSource.MANUAL,
                    )
                }
                    .onFailure { failures++ }
            }
            if (failures < uris.size) UploadScheduler.enqueue(appContext)
            _state.update { it.copy(isUploading = false, snackbar = if (failures == 0) UPLOAD_QUEUED else UPLOAD_PARTIAL) }
            reload()
        }
    }

    fun addFavorite(fileId: String) {
        viewModelScope.launch {
            runCatching { cloudRepository.addFavorite(fileId) }
                .onSuccess { _state.update { it.copy(snackbar = FAVORITE_ADDED) } }
                .onFailure { e -> _state.update { it.copy(snackbar = e.message) } }
        }
    }

    fun addToVault(asset: MediaAsset) {
        (appContext as BarkCloudApplication).vaultStore.add(VaultItem.from(asset))
        _state.update { it.copy(snackbar = VAULT_ADDED) }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        private const val UPLOAD_PARTIAL = "Часть файлов не загрузилась"
        private const val UPLOAD_QUEUED = "Загрузка поставлена в очередь"
        private const val FAVORITE_ADDED = "Добавлено в избранное"
        private const val VAULT_ADDED = "Добавлено в vault"

        fun factory(kind: CloudMediaKind): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return MediaGridViewModel(app, app.cloudRepository, kind) as T
            }
        }
    }
}
