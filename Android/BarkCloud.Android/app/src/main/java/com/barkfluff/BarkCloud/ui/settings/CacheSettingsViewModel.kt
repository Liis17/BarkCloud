package com.barkfluff.BarkCloud.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import coil3.SingletonImageLoader
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.cache.FileCacheSettings
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class CacheSettingsUiState(
    val isLoading: Boolean = true,
    val cacheSize: Long = 0,
    val entryCount: Int = 0,
    val previewCacheSize: Long = 0,
    val previewEntryCount: Int = 0,
    val maxCacheBytes: Long = FileCacheSettings.DEFAULT_MAX_BYTES,
    val staleMaxAgeMillis: Long = FileCacheSettings.DEFAULT_STALE_AGE_MILLIS,
    val snackbar: String? = null,
)

class CacheSettingsViewModel(
    private val app: BarkCloudApplication,
    private val fileCache: FileCacheService,
    private val settings: FileCacheSettings,
) : ViewModel() {

    private val _state = MutableStateFlow(CacheSettingsUiState())
    val state: StateFlow<CacheSettingsUiState> = _state.asStateFlow()

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            _state.update {
                it.copy(
                    isLoading = false,
                    cacheSize = fileCache.totalSize(),
                    entryCount = fileCache.entryCount(),
                    previewCacheSize = fileCache.previewSize(),
                    previewEntryCount = fileCache.previewEntryCount(),
                    maxCacheBytes = settings.maxCacheBytes,
                    staleMaxAgeMillis = settings.staleMaxAgeMillis,
                )
            }
        }
    }

    fun setMaxCacheBytes(value: Long) {
        settings.maxCacheBytes = value
        viewModelScope.launch {
            fileCache.enforceSizeLimit()
            load()
        }
    }

    fun setStaleMaxAge(value: Long) {
        settings.staleMaxAgeMillis = value
        load()
    }

    fun clearStale() {
        viewModelScope.launch {
            val removed = fileCache.clearStale()
            _state.update { it.copy(snackbar = app.getString(com.barkfluff.BarkCloud.R.string.cache_removed_count, removed)) }
            load()
        }
    }

    fun clearAll() {
        viewModelScope.launch {
            fileCache.clearAll()
            SingletonImageLoader.get(app).memoryCache?.clear()
            SingletonImageLoader.get(app).diskCache?.clear()
            _state.update { it.copy(snackbar = app.getString(com.barkfluff.BarkCloud.R.string.cache_cleared)) }
            load()
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return CacheSettingsViewModel(app, app.fileCache, app.fileCacheSettings) as T
            }
        }
    }
}
