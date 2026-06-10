package com.barkfluff.BarkCloud.ui.gallery

import android.content.Context
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.MediaHasher
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.concurrent.ConcurrentHashMap

data class GalleryUiState(
    val permissionGranted: Boolean = false,
    val isLoading: Boolean = false,
    val items: List<DeviceMedia> = emptyList(),
    val selecting: Boolean = false,
    val selected: Set<Long> = emptySet(),
    val cloudPresence: Map<Long, Boolean> = emptyMap(),
    val isUploading: Boolean = false,
    val uploadDone: Int = 0,
    val uploadTotal: Int = 0,
    val autoUploadEnabled: Boolean = false,
    val lastAutoUploadCount: Int = 0,
    val snackbar: String? = null,
) {
    val reclaimableCount: Int get() = items.count { cloudPresence[it.id] == true }
}

class GalleryViewModel(
    private val appContext: Context,
    private val cloudRepository: CloudRepository,
    private val autoUploadSettings: AutoUploadSettings,
) : ViewModel() {

    private val _state = MutableStateFlow(GalleryUiState())
    val state: StateFlow<GalleryUiState> = _state.asStateFlow()

    private val idToHash = ConcurrentHashMap<Long, String>()
    private val hashPresence = ConcurrentHashMap<String, Boolean>()
    private val pending = LinkedHashSet<String>()
    private var flushJob: Job? = null

    init {
        _state.update {
            it.copy(
                autoUploadEnabled = autoUploadSettings.enabled,
                lastAutoUploadCount = autoUploadSettings.lastUploadedCount,
            )
        }
    }

    fun onPermissionResult(granted: Boolean) {
        _state.update { it.copy(permissionGranted = granted) }
        if (granted && _state.value.items.isEmpty()) load()
    }

    fun load() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            val items = DeviceMediaStore.query(appContext)
            _state.update { it.copy(isLoading = false, items = items) }
        }
    }

    fun toggleSelecting() {
        _state.update {
            if (it.selecting) it.copy(selecting = false, selected = emptySet())
            else it.copy(selecting = true)
        }
    }

    fun startSelecting(id: Long) {
        _state.update { it.copy(selecting = true, selected = it.selected + id) }
    }

    fun toggle(id: Long) {
        _state.update {
            val sel = if (it.selected.contains(id)) it.selected - id else it.selected + id
            it.copy(selected = sel)
        }
    }

    fun uploadSelected() {
        val current = _state.value
        val targets = current.items.filter { current.selected.contains(it.id) }
        if (targets.isEmpty()) return
        _state.update { it.copy(isUploading = true, uploadDone = 0, uploadTotal = targets.size) }
        viewModelScope.launch {
            var failures = 0
            targets.forEachIndexed { index, media ->
                runCatching { (appContext as BarkCloudApplication).uploadQueue.enqueue(media.uri, media.name) }
                    .onFailure { failures++ }
                    .onSuccess { idToHash[media.id]?.let { h -> hashPresence[h] = true } }
                _state.update { it.copy(uploadDone = index + 1) }
            }
            if (failures < targets.size) UploadScheduler.enqueue(appContext)
            _state.update {
                it.copy(
                    isUploading = false,
                    selecting = false,
                    selected = emptySet(),
                    snackbar = if (failures == 0) UPLOAD_OK else UPLOAD_PARTIAL,
                    cloudPresence = it.cloudPresence + targets.associate { m -> m.id to (idToHash[m.id]?.let { h -> hashPresence[h] } ?: true) },
                )
            }
        }
    }

    fun setAutoUpload(enabled: Boolean) {
        autoUploadSettings.enabled = enabled
        if (enabled) {
            AutoUploadScheduler.enable(appContext)
        } else {
            AutoUploadScheduler.disable(appContext)
        }
        _state.update {
            it.copy(
                autoUploadEnabled = enabled,
                lastAutoUploadCount = autoUploadSettings.lastUploadedCount,
                snackbar = if (enabled) AUTO_UPLOAD_ON else AUTO_UPLOAD_OFF,
            )
        }
    }

    fun onDeviceCopiesDeleted(count: Int) {
        _state.update { it.copy(snackbar = appContext.getString(com.barkfluff.BarkCloud.R.string.gallery_device_copies_deleted, count)) }
        load()
    }

    fun observeCloudPresence(media: DeviceMedia) {
        if (_state.value.cloudPresence.containsKey(media.id)) return
        viewModelScope.launch {
            val hash = idToHash[media.id]
                ?: withContext(Dispatchers.IO) { MediaHasher.sha256(appContext, media.uri) }?.also {
                    idToHash[media.id] = it
                }
                ?: return@launch
            hashPresence[hash]?.let { exists ->
                updatePresence(media.id, exists)
                return@launch
            }
            enqueue(hash)
        }
    }

    private fun enqueue(hash: String) {
        synchronized(pending) { pending.add(hash) }
        flushJob?.cancel()
        flushJob = viewModelScope.launch {
            delay(400)
            flush()
        }
    }

    private suspend fun flush() {
        val batch = synchronized(pending) { val copy = pending.toList(); pending.clear(); copy }
        if (batch.isEmpty()) return
        batch.chunked(500).forEach { chunk ->
            val result = runCatching { cloudRepository.checkFileHashes(chunk) }.getOrNull() ?: return@forEach
            result.forEach { (h, exists) -> hashPresence[h] = exists }
            _state.update { st ->
                val map = st.cloudPresence.toMutableMap()
                idToHash.forEach { (id, h) -> result[h]?.let { map[id] = it } }
                st.copy(cloudPresence = map)
            }
        }
    }

    private fun updatePresence(id: Long, exists: Boolean) {
        _state.update { it.copy(cloudPresence = it.cloudPresence + (id to exists)) }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        private const val UPLOAD_OK = "Загрузка поставлена в очередь"
        private const val UPLOAD_PARTIAL = "Часть файлов не удалось поставить в очередь"
        private const val AUTO_UPLOAD_ON = "Автозагрузка включена"
        private const val AUTO_UPLOAD_OFF = "Автозагрузка выключена"

        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return GalleryViewModel(app, app.cloudRepository, app.autoUploadSettings) as T
            }
        }
    }
}
