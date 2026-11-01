package com.barkfluff.BarkCloud.ui.media

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository

import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.CloudLibraryChange
import com.barkfluff.BarkCloud.data.cloud.CloudLibraryChanges
import com.barkfluff.BarkCloud.data.cloud.CloudRepository

import com.barkfluff.BarkCloud.data.gallery.CloudPresenceChecker
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteExecutor
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteItem
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteMode
import com.barkfluff.BarkCloud.data.gallery.DeviceDeletePlan
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.gallery.MediaHasher
import com.barkfluff.BarkCloud.data.gallery.cloudStateOf
import com.barkfluff.BarkCloud.data.gallery.resolveCloudCopies
import com.barkfluff.BarkCloud.data.gallery.hiddenCopyIds
import com.barkfluff.BarkCloud.data.gallery.planDeviceDelete
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.upload.LiveAlbumRequest
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.data.upload.UploadSource
import com.barkfluff.BarkCloud.data.vault.VaultStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

/** Прогресс постановки ручной загрузки в очередь (копирование файлов); отмена останавливает новые постановки. */
internal data class StagingProgress(val done: Int, val total: Int)

/** Подтверждаемое удаление: режим и план, из которого видно число облачных копий. */
internal data class DeleteConfirmation(val mode: DeviceDeleteMode, val plan: DeviceDeletePlan)

internal data class DeviceFoldersUiState(
    val folders: List<DeviceFolder> = emptyList(),
    /** Статусы облака из Room; BADGE «в облаке» показывается по IN_CLOUD. */
    val cloudStates: Map<String, MediaCloudStatus> = emptyMap(),
    val selected: Set<String> = emptySet(),
    val busy: Boolean = false,
    val staging: StagingProgress? = null,
    val confirmation: DeleteConfirmation? = null,
    /** Среди целей есть скрытые копии: до действия нужна разблокировка vault. */
    val unlockRequired: Boolean = false,
    /** Запрос системного подтверждения удаления локальных копий (очищается после показа). */
    val systemDeleteUris: List<Uri>? = null,
    val snackbar: String? = null,
)

/**
 * Операции над фото устройства из папок: ручная загрузка, добавление в альбом и два вида удаления.
 * Настройки автозагрузки папок не меняются. Облачные копии определяются по хешу непосредственно
 * перед действием; «неизвестно» блокирует только связанное действие.
 */
internal class DeviceFoldersViewModel(
    private val appContext: Context,
    private val cloud: CloudRepository,
    private val albums: AlbumRepository,
    private val queue: UploadQueueStore,
    private val mediaDao: MediaCloudStateDao,
    private val vault: VaultStore,
    private val executor: DeviceDeleteExecutor,
) : ViewModel() {

    private val _state = MutableStateFlow(DeviceFoldersUiState())
    val state: StateFlow<DeviceFoldersUiState> = _state.asStateFlow()

    private val presence = CloudPresenceChecker(
        scope = viewModelScope,
        mediaDao = mediaDao,
        hashFile = { media -> withContext(Dispatchers.IO) { MediaHasher.sha256(appContext, media.uri) } },
        presence = { hashes -> cloud.checkFileHashesDetailed(hashes) },
    )

    private var media: List<DeviceMedia> = emptyList()

    /** Ожидающие действия переживают пересоздание экрана (живут в ViewModel). */
    private var pendingPlan: DeviceDeletePlan? = null
    private var pendingHashes: Map<String, String> = emptyMap()
    private var pendingAlbumId: String? = null
    private var stagingCancelled = false

    init {
        viewModelScope.launch {
            mediaDao.observeAll().collect { rows ->
                _state.update { it.copy(cloudStates = rows.associate { row -> row.mediaKey to row.status }) }
            }
        }
    }

    /** Перечитывает локальные папки; выбранные, которых больше нет, снимаются. */
    suspend fun reload(images: Boolean, videos: Boolean) {
        val items = withContext(Dispatchers.IO) { DeviceMediaStore.query(appContext, images, videos) }
        media = items
        val keys = items.map { it.mediaKey }.toSet()
        _state.update { it.copy(folders = deviceFolders(items), selected = it.selected.filter { key -> key in keys }.toSet()) }
    }

    /** Проверка присутствия видимой ячейки; [force] — ревалидация при открытии или обновлении. */
    fun requestPresence(item: DeviceMedia, force: Boolean) = presence.request(item, force)

    fun setSelection(keys: Set<String>) = _state.update { if (it.busy || it.staging != null) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.busy) it else it.copy(selected = emptySet()) }

    /** Ручная загрузка. Уже существующие в облаке копии не загружаются повторно. */
    fun uploadSelected() = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val toUpload = targets.filter { cloudByKey[it.mediaKey] == CloudCopy.Absent }
        val inCloud = targets.count { cloudByKey[it.mediaKey] is CloudCopy.Present }
        val unknown = targets.size - toUpload.size - inCloud
        var queued = 0
        var notQueued = 0
        stagingCancelled = false
        for ((index, item) in toUpload.withIndex()) {
            if (stagingCancelled) break
            _state.update { it.copy(staging = StagingProgress(index, toUpload.size)) }
            try {
                val hash = presence.hashOf(item)
                queue.enqueue(
                    uri = item.uri,
                    fileName = item.name,
                    source = UploadSource.MANUAL,
                    destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                    mediaKey = item.mediaKey,
                    mediaHash = hash,
                )
                mediaDao.upsert(cloudStateOf(item, hash, MediaCloudStatus.QUEUED))
                queued++
            } catch (error: CancellationException) {
                throw error
            } catch (_: IOException) {
                // Нехватка места или недоступный файл: пакет останавливается, уже поставленное остаётся.
                break
            }
        }
        _state.update { it.copy(staging = null) }
        notQueued = toUpload.size - queued
        if (queued > 0) UploadScheduler.enqueue(appContext)
        _state.update {
            it.copy(
                selected = emptySet(),
                snackbar = appContext.getString(R.string.device_upload_result, queued, inCloud, unknown + notQueued),
            )
        }
    }

    fun cancelStaging() {
        stagingCancelled = true
    }

    /**
     * Добавление в альбом. Живая задача получает альбом; иначе существующая копия добавляется
     * по детерминированному ID; без копии ставится загрузка с альбомом.
     */
    fun addSelectedToAlbum(albumId: String, unlocked: Boolean = false) = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val hidden = cloudByKey.values.flatMap { (it as? CloudCopy.Present)?.fileIds.orEmpty() }.any { it in vaultIds() }
        if (hidden && !unlocked) {
            pendingAlbumId = albumId
            _state.update { it.copy(unlockRequired = true) }
            return@runOperation
        }
        var added = 0
        var queued = 0
        var busy = 0
        var failed = 0
        for (item in targets) {
            try {
                when (queue.requestAlbumForLive(item.mediaKey, albumId)) {
                    LiveAlbumRequest.Attached -> {
                        queued++
                        continue
                    }
                    LiveAlbumRequest.Busy -> {
                        busy++
                        continue
                    }
                    LiveAlbumRequest.NoLiveJob -> Unit
                }
                when (val copy = cloudByKey[item.mediaKey]) {
                    is CloudCopy.Present -> {
                        albums.addItems(albumId, listOf(copy.fileId))
                        added++
                    }
                    CloudCopy.Absent -> {
                        val hash = presence.hashOf(item)
                        queue.enqueue(
                            uri = item.uri,
                            fileName = item.name,
                            source = UploadSource.ALBUM,
                            destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                            albumId = albumId,
                            mediaKey = item.mediaKey,
                            mediaHash = hash,
                        )
                        mediaDao.upsert(cloudStateOf(item, hash, MediaCloudStatus.QUEUED))
                        queued++
                    }
                    CloudCopy.Unknown, null -> failed++
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                failed++
            }
        }
        if (queued > 0) UploadScheduler.enqueue(appContext)
        _state.update {
            it.copy(
                selected = emptySet(),
                snackbar = appContext.getString(R.string.device_album_result, added, queued, busy, failed),
            )
        }
    }

    /** Подготовка удаления: состояния облака перепроверяются сейчас, затем подтверждение с числом копий. */
    fun prepareDelete(mode: DeviceDeleteMode, unlocked: Boolean = false) = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val plan = planDeviceDelete(mode, targets.map { DeviceDeleteItem(it.mediaKey, cloudByKey[it.mediaKey] ?: CloudCopy.Unknown) })
        val hidden = plan.hiddenCopyIds(vaultIds())
        pendingPlan = plan
        pendingHashes = targets.mapNotNull { item -> presence.hashOf(item)?.let { item.mediaKey to it } }.toMap()
        if (hidden.isNotEmpty() && !unlocked) {
            _state.update { it.copy(unlockRequired = true) }
            return@runOperation
        }
        _state.update { it.copy(confirmation = DeleteConfirmation(mode, plan)) }
    }

    fun cancelDelete() {
        pendingPlan = null
        pendingHashes = emptyMap()
        _state.update { it.copy(confirmation = null) }
    }

    /** Подтверждение пользователем: дальше системный запрос на удаление локальных копий. */
    fun confirmDelete() {
        val plan = pendingPlan ?: return
        val uris = plan.devicePhotos.mapNotNull { key -> media.firstOrNull { it.mediaKey == key }?.uri }
        _state.update { it.copy(confirmation = null, systemDeleteUris = uris.takeIf { it.isNotEmpty() }) }
        if (uris.isEmpty()) {
            pendingPlan = null
            _state.update { it.copy(snackbar = appContext.getString(R.string.device_delete_nothing)) }
        }
    }

    /** Системный запрос показан; повторно не запускаем. */
    fun systemDeleteRequested() = _state.update { it.copy(systemDeleteUris = null) }

    /**
     * Ответ системного подтверждения. Отказ не трогает облако. Одобренное удаление выполняется
     * всегда, даже если другая операция идёт: нельзя оставить локальные копии удалёнными без облака.
     */
    fun onSystemDeleteResult(approved: Boolean) {
        val plan = pendingPlan
        val hashes = pendingHashes
        pendingPlan = null
        pendingHashes = emptyMap()
        if (!approved || plan == null) return
        _state.update { it.copy(busy = true) }
        viewModelScope.launch {
            try {
                val report = executor.finish(plan, hashes)
                media = media.filterNot { it.mediaKey in plan.devicePhotos }
                _state.update {
                    it.copy(
                        folders = deviceFolders(media),
                        selected = emptySet(),
                        snackbar = appContext.getString(
                            R.string.device_delete_result,
                            report.devicePhotos,
                            report.cloudCopiesDeleted,
                            report.failedPhotos,
                        ),
                    )
                }
                CloudLibraryChanges.publish(CloudLibraryChange.Changed)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            } finally {
                _state.update { it.copy(busy = false) }
            }
        }
    }

    /** Повтор облачных очисток, оставшихся после частичных ошибок. */
    fun retryPendingCloudCleanup() = runOperation {
        val failed = executor.retryPending()
        _state.update {
            it.copy(snackbar = if (failed == 0) appContext.getString(R.string.device_cleanup_done) else appContext.getString(R.string.device_cleanup_pending, failed))
        }
    }

    /** Итог разблокировки vault: при успехе продолжается отложенное действие, при отказе сбрасывается. */
    fun onUnlockResult(granted: Boolean) {
        _state.update { it.copy(unlockRequired = false) }
        val album = pendingAlbumId
        pendingAlbumId = null
        if (!granted) {
            pendingPlan = null
            pendingHashes = emptyMap()
            return
        }
        if (album != null) {
            addSelectedToAlbum(album, unlocked = true)
        } else if (pendingPlan != null) {
            pendingPlan?.let { plan -> _state.update { it.copy(confirmation = DeleteConfirmation(plan.mode, plan)) } }
        }
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    private fun selectedMedia(): List<DeviceMedia> {
        val keys = _state.value.selected
        return media.filter { it.mediaKey in keys }
    }

    private fun vaultIds(): Set<String> = vault.items().map { it.fileId }.toSet()

    /** Облачное состояние каждого фото: хеш из кеша/вычисления, затем пакетная проверка. */
    private suspend fun resolveCloud(items: List<DeviceMedia>): Map<String, CloudCopy> =
        resolveCloudCopies(items, hashOf = presence::hashOf, presence = cloud::checkFileHashesDetailed)

    private fun runOperation(block: suspend () -> Unit) {
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

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return DeviceFoldersViewModel(
                    appContext = app,
                    cloud = app.cloudRepository,
                    albums = app.albumRepository,
                    queue = app.uploadQueue,
                    mediaDao = BarkCloudDatabase.get(app).mediaCloudStateDao(),
                    vault = app.vaultStore,
                    executor = app.deviceDeleteExecutor,
                ) as T
            }
        }
    }
}
