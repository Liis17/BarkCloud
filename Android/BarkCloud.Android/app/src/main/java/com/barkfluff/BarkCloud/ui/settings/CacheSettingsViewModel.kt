package com.barkfluff.BarkCloud.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import coil3.SingletonImageLoader
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cache.CacheCategory
import com.barkfluff.BarkCloud.data.cache.CacheCategoryStats
import com.barkfluff.BarkCloud.data.cache.FileCacheStats
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
    val originalCacheSize: Long = 0,
    val originalEntryCount: Int = 0,
    val legacyPreviewSize: Long = 0,
    val legacyPreviewEntryCount: Int = 0,
    val isOverLimit: Boolean = false,
    val maxCacheBytes: Long = FileCacheSettings.DEFAULT_MAX_BYTES,
    val staleMaxAgeMillis: Long = FileCacheSettings.DEFAULT_STALE_AGE_MILLIS,
    val snackbar: String? = null,
)

internal fun cacheStatisticsToUiState(
    stats: FileCacheStats,
    maxCacheBytes: Long,
    staleMaxAgeMillis: Long,
): CacheSettingsUiState {
    val previews = stats.categories[CacheCategory.PREVIEW] ?: CacheCategoryStats(0, 0)
    val jpegViews = stats.categories[CacheCategory.JPEG_VIEW] ?: CacheCategoryStats(0, 0)
    val originals = stats.categories[CacheCategory.ORIGINAL] ?: CacheCategoryStats(0, 0)
    return CacheSettingsUiState(
        isLoading = false,
        cacheSize = stats.totalSizeBytes,
        entryCount = stats.entryCount,
        previewCacheSize = previews.sizeBytes + jpegViews.sizeBytes,
        previewEntryCount = previews.entryCount + jpegViews.entryCount,
        originalCacheSize = originals.sizeBytes,
        originalEntryCount = originals.entryCount,
        legacyPreviewSize = stats.legacyPreviewSizeBytes,
        legacyPreviewEntryCount = stats.legacyPreviewEntryCount,
        isOverLimit = stats.isOverLimit,
        maxCacheBytes = maxCacheBytes,
        staleMaxAgeMillis = staleMaxAgeMillis,
    )
}

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
            val loaded = cacheStatisticsToUiState(
                stats = fileCache.statistics(),
                maxCacheBytes = settings.maxCacheBytes,
                staleMaxAgeMillis = settings.staleMaxAgeMillis,
            )
            _state.update { loaded.copy(snackbar = it.snackbar) }
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
