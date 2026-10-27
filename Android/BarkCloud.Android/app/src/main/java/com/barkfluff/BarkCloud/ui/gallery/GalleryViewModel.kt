package com.barkfluff.BarkCloud.ui.gallery

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.NotificationPermissionRequests
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.gallery.MediaHasher
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.data.upload.UploadSource
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.util.concurrent.ConcurrentHashMap

data class GalleryUiState(
    val permissionGranted: Boolean = false,
    val isLoading: Boolean = false,
    val items: List<DeviceMedia> = emptyList(),
    val selecting: Boolean = false,
    val selected: Set<String> = emptySet(),
    val cloudStates: Map<String, MediaCloudStatus> = emptyMap(),
    val isQueueing: Boolean = false,
    val autoUploadPolicy: AutoUploadNetworkPolicy = AutoUploadNetworkPolicy.WIFI_ONLY,
    val lastAutoUploadCount: Int = 0,
    val snackbar: String? = null,
) {
    val reclaimableCount: Int get() = items.count { cloudStates[it.mediaKey] == MediaCloudStatus.IN_CLOUD }
    internal val selectionActions: PhotoSelectionActions get() = photoSelectionActions(selected)
}

class GalleryViewModel(
    private val appContext: Context,
    private val cloudRepository: CloudRepository,
    private val autoUploadSettings: AutoUploadSettings,
) : ViewModel() {

    private val mediaDao: MediaCloudStateDao = BarkCloudDatabase.get(appContext).mediaCloudStateDao()
    private val _state = MutableStateFlow(GalleryUiState())
    val state: StateFlow<GalleryUiState> = _state.asStateFlow()
    private val deletionCoordinator = GalleryDeletionCoordinator(
        scope = viewModelScope,
        deleteCloudFile = cloudRepository::deleteUserMedia,
        onDeviceFilesDeleted = ::onDeviceCopiesDeleted,
        onCloudFileDeleted = ::onCloudFileDeleted,
    )
    internal val deletionState: StateFlow<GalleryDeletionState> = deletionCoordinator.state

    private val idToHash = ConcurrentHashMap<String, String>()
    private val hashPresence = ConcurrentHashMap<String, Boolean>()
    private val pendingHashes = LinkedHashSet<String>()
    private val mediaLoadMutex = Mutex()
    private var flushJob: Job? = null

    init {
        _state.update {
            it.copy(
                autoUploadPolicy = autoUploadSettings.policy,
                lastAutoUploadCount = autoUploadSettings.lastUploadedCount,
            )
        }
        viewModelScope.launch {
            mediaDao.observeAll().collectLatest { states ->
                val byId = states.associate { it.mediaKey to it.status }
                states.forEach { state -> state.hash?.let { hashPresence[it] = state.status == MediaCloudStatus.IN_CLOUD } }
                _state.update { it.copy(cloudStates = byId) }
            }
        }
    }

    fun onPermissionResult(granted: Boolean) {
        _state.update { it.copy(permissionGranted = granted) }
        if (granted && _state.value.items.isEmpty()) load()
    }

    fun load() {
        viewModelScope.launch {
            reloadAndWait()
        }
    }

    suspend fun reloadAndWait() = mediaLoadMutex.withLock {
        _state.update { it.copy(isLoading = true) }
        try {
            val items = DeviceMediaStore.query(appContext)
            _state.update { it.copy(isLoading = false, items = items) }
        } catch (e: kotlinx.coroutines.CancellationException) {
            _state.update { it.copy(isLoading = false) }
            throw e
        } catch (e: Exception) {
            _state.update { it.copy(isLoading = false, snackbar = e.message) }
        }
    }

    fun toggleSelecting() = clearSelection()

    fun startSelecting(key: String) = _state.update {
        if (deletionState.value.isBusy) return@update it
        val selection = PhotoSelection(it.selected).start(key)
        it.copy(selecting = selection.isSelecting, selected = selection.keys)
    }

    fun toggle(key: String) = _state.update {
        if (deletionState.value.isBusy) return@update it
        val selection = PhotoSelection(it.selected).toggle(key)
        it.copy(selecting = selection.isSelecting, selected = selection.keys)
    }

    fun removeSelection(keys: Set<String>) = _state.update {
        if (deletionState.value.isBusy) return@update it
        val selection = PhotoSelection(it.selected).remove(keys)
        it.copy(selecting = selection.isSelecting, selected = selection.keys)
    }

    fun clearSelection() = _state.update {
        if (deletionState.value.isBusy) return@update it
        it.copy(selecting = false, selected = emptySet())
    }

    fun uploadSelected() {
        if (deletionState.value.isBusy || _state.value.isQueueing) return
        val selectedDeviceKeys = _state.value.selected
            .filter { it.startsWith(DEVICE_SELECTION_PREFIX) }
            .map { it.removePrefix(DEVICE_SELECTION_PREFIX) }
            .toSet()
        val targets = _state.value.items.filter { it.mediaKey in selectedDeviceKeys }
        if (targets.isEmpty()) return
        _state.update { it.copy(isQueueing = true) }
        viewModelScope.launch {
            val app = appContext as BarkCloudApplication
            var failures = 0
            for (media in targets) {
                val hash = idToHash[media.mediaKey]
                    ?: withContext(Dispatchers.IO) { MediaHasher.sha256(appContext, media.uri) }?.also { idToHash[media.mediaKey] = it }
                runCatching {
                    app.uploadQueue.enqueue(
                        uri = media.uri,
                        fileName = media.name,
                        source = UploadSource.GALLERY,
                        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                        mediaKey = media.mediaKey,
                        mediaHash = hash,
                    )
                    mediaDao.upsert(mediaState(media, hash, MediaCloudStatus.QUEUED))
                }.onFailure { failures++ }
            }
            if (failures < targets.size) UploadScheduler.enqueue(appContext)
            _state.update {
                it.copy(
                    isQueueing = false,
                    selecting = false,
                    selected = emptySet(),
                    snackbar = if (failures == 0) UPLOAD_QUEUED else UPLOAD_PARTIAL,
                )
            }
        }
    }

    fun setAutoUploadPolicy(policy: AutoUploadNetworkPolicy) {
        autoUploadSettings.policy = policy
        val app = appContext as BarkCloudApplication
        viewModelScope.launch {
            if (policy == AutoUploadNetworkPolicy.OFF) {
                AutoUploadScheduler.disable(appContext)
                app.uploadQueue.pauseBackup()
            } else {
                NotificationPermissionRequests.request()
                app.uploadQueue.resumeBackup()
                AutoUploadScheduler.apply(appContext, policy)
            }
        }
        _state.update {
            it.copy(
                autoUploadPolicy = policy,
                lastAutoUploadCount = autoUploadSettings.lastUploadedCount,
                snackbar = if (policy == AutoUploadNetworkPolicy.OFF) AUTO_UPLOAD_OFF else AUTO_UPLOAD_ON,
            )
        }
    }

    fun refreshAutoUploadPolicy() = _state.update {
        it.copy(
            autoUploadPolicy = autoUploadSettings.policy,
            lastAutoUploadCount = autoUploadSettings.lastUploadedCount,
        )
    }

    fun observeCloudPresence(media: DeviceMedia) {
        viewModelScope.launch {
            val existing = mediaDao.byKey(media.mediaKey)
            if (existing != null &&
                existing.dateModifiedSeconds == media.dateModifiedSeconds &&
                existing.sizeBytes == media.sizeBytes &&
                existing.status in setOf(MediaCloudStatus.QUEUED, MediaCloudStatus.UPLOADING, MediaCloudStatus.IN_CLOUD)
            ) return@launch
            val hash = idToHash[media.mediaKey]
                ?: withContext(Dispatchers.IO) { MediaHasher.sha256(appContext, media.uri) }?.also { idToHash[media.mediaKey] = it }
                ?: return@launch
            hashPresence[hash]?.let { exists ->
                mediaDao.upsert(mediaState(media, hash, if (exists) MediaCloudStatus.IN_CLOUD else MediaCloudStatus.NOT_IN_CLOUD))
                return@launch
            }
            mediaDao.upsert(mediaState(media, hash, MediaCloudStatus.CHECKING))
            enqueueHash(hash)
        }
    }

    /** Revalidates every candidate before Android shows its destructive system dialog. */
    fun prepareDeviceCopyDeletion(onReady: (List<Uri>, List<String>) -> Unit) {
        viewModelScope.launch {
            val items = _state.value.items.filter { _state.value.cloudStates[it.mediaKey] == MediaCloudStatus.IN_CLOUD }
            val states = mediaDao.all().associateBy { it.mediaKey }
            val hashes = items.mapNotNull { states[it.mediaKey]?.hash }.distinct()
            val presence = runCatching { cloudRepository.checkFileHashes(hashes) }.getOrDefault(emptyMap())
            val confirmed = items.filter { media ->
                val state = states[media.mediaKey]
                val exists = state?.hash?.let { presence[it] } == true
                if (!exists && state != null) mediaDao.upsert(state.copy(status = MediaCloudStatus.NOT_IN_CLOUD))
                exists
            }
            if (confirmed.isNotEmpty()) onReady(confirmed.map { it.uri }, confirmed.map { it.mediaKey })
        }
    }

    internal fun selectedDeviceTargets(mediaKeys: List<String>): List<DeviceMedia> {
        val selectedMediaKeys = mediaKeys.toSet()
        return _state.value.items.filter { it.mediaKey in selectedMediaKeys }
    }

    fun onDeviceCopiesDeleted(mediaKeys: List<String>) {
        val selectedKeys = mediaKeys.map { "$DEVICE_SELECTION_PREFIX$it" }.toSet()
        _state.update {
            val selection = PhotoSelection(it.selected).remove(selectedKeys)
            it.copy(
                items = it.items.filterNot { media -> media.mediaKey in mediaKeys },
                selecting = selection.isSelecting,
                selected = selection.keys,
            )
        }
        viewModelScope.launch { mediaKeys.forEach { mediaDao.delete(it) } }
    }

    suspend fun addFavorites(fileIds: List<String>): Set<String> {
        if (deletionState.value.isBusy) return emptySet()
        val added = linkedSetOf<String>()
        fileIds.forEach { fileId ->
            try {
                cloudRepository.addFavorite(fileId)
                added += fileId
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (_: Exception) {
                // Ошибочные элементы остаются выбранными для повтора.
            }
        }
        return added
    }

    internal fun beginSystemDelete(pending: PendingPhotoDelete): Boolean =
        !_state.value.isQueueing && deletionCoordinator.beginSystemConfirmation(pending)

    internal fun abortSystemDelete() = deletionCoordinator.abortSystemConfirmation()

    internal fun completeSystemDelete(pending: PendingPhotoDelete, approved: Boolean) =
        deletionCoordinator.completeSystemConfirmation(pending, approved)

    internal fun deleteCloudOnly(fileIds: List<String>, totalCount: Int = fileIds.size): Boolean {
        if (_state.value.isQueueing) return false
        return deletionCoordinator.deleteCloudOnly(
            PendingPhotoDelete(
                deviceMediaKeys = emptyList(),
                cloudFileIds = fileIds,
                totalCount = totalCount,
            ),
        )
    }

    internal fun acknowledgeDeletedCloudIds(ids: Set<String>) = deletionCoordinator.acknowledgeDeletedCloudIds(ids)

    internal fun acknowledgeDeleteResult(id: Int) = deletionCoordinator.acknowledgeResult(id)

    private fun onCloudFileDeleted(fileId: String) {
        val key = "$CLOUD_SELECTION_PREFIX$fileId"
        _state.update {
            val selection = PhotoSelection(it.selected).remove(setOf(key))
            it.copy(selecting = selection.isSelecting, selected = selection.keys)
        }
    }

    private fun enqueueHash(hash: String) {
        synchronized(pendingHashes) { pendingHashes.add(hash) }
        flushJob?.cancel()
        flushJob = viewModelScope.launch {
            delay(400)
            flushPresence()
        }
    }

    private suspend fun flushPresence() {
        val hashes = synchronized(pendingHashes) { pendingHashes.toList().also { pendingHashes.clear() } }
        if (hashes.isEmpty()) return
        hashes.chunked(500).forEach { chunk ->
            val result = runCatching { cloudRepository.checkFileHashes(chunk) }.getOrNull() ?: return@forEach
            result.forEach { (hash, exists) -> hashPresence[hash] = exists }
            _state.value.items.forEach { media ->
                val hash = idToHash[media.mediaKey] ?: return@forEach
                result[hash]?.let { exists ->
                    mediaDao.upsert(mediaState(media, hash, if (exists) MediaCloudStatus.IN_CLOUD else MediaCloudStatus.NOT_IN_CLOUD))
                }
            }
        }
    }

    private fun mediaState(media: DeviceMedia, hash: String?, status: MediaCloudStatus): MediaCloudState =
        MediaCloudState(
            mediaKey = media.mediaKey,
            mediaId = media.id,
            isVideo = media.isVideo,
            dateModifiedSeconds = media.dateModifiedSeconds,
            sizeBytes = media.sizeBytes,
            hash = hash,
            status = status,
            cloudFileId = null,
        )

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    companion object {
        private const val UPLOAD_QUEUED = "Загрузка поставлена в очередь"
        private const val UPLOAD_PARTIAL = "Часть файлов не удалось поставить в очередь"
        private const val AUTO_UPLOAD_ON = "Автозагрузка включена"
        private const val AUTO_UPLOAD_OFF = "Автозагрузка отключена"

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
