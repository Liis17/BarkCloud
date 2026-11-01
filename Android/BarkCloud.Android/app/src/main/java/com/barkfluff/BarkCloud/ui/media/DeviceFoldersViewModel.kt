package com.barkfluff.BarkCloud.ui.media

import android.content.Context
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cache.CacheScope
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.CloudLibraryChange
import com.barkfluff.BarkCloud.data.cloud.CloudLibraryChanges
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.gallery.CloudPresenceChecker
import com.barkfluff.BarkCloud.data.gallery.CloudRunResult
import com.barkfluff.BarkCloud.data.gallery.DeleteOpPhase
import com.barkfluff.BarkCloud.data.gallery.DeleteOperation
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteExecutor
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteItem
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteMode
import com.barkfluff.BarkCloud.data.gallery.DeviceDeletePlan
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.HiddenDeviceSession
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.gallery.MediaHasher
import com.barkfluff.BarkCloud.data.gallery.cloudStateOf
import com.barkfluff.BarkCloud.data.gallery.hiddenCopyIds
import com.barkfluff.BarkCloud.data.gallery.planDeviceDelete
import com.barkfluff.BarkCloud.data.gallery.resolveCloudCopies
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.upload.LiveAlbumRequest
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.data.upload.UploadSource
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

/** Прогресс постановки ручной загрузки в очередь; отмена останавливает только новые постановки. */
internal data class StagingProgress(val done: Int, val total: Int, val cancelled: Boolean = false)

/** Подтверждаемое удаление: режим, план (облачные ID подтверждены) и хеши фото. */
internal data class DeleteConfirmation(val mode: DeviceDeleteMode, val plan: DeviceDeletePlan, val hashes: Map<String, String>)

/** Действие, ожидающее разблокировки Vault. */
internal sealed interface UnlockReason {
    data class ConfirmDelete(val confirmation: DeleteConfirmation) : UnlockReason
    data class AddAlbum(val albumId: String) : UnlockReason
    data class ResumeOperation(val operationId: String) : UnlockReason
}

/** Запрос системного подтверждения удаления локальных копий для уже записанной операции. */
internal data class SystemDeleteRequest(val operationId: String, val uris: List<Uri>)

/** Сводка незавершённой операции для баннера экрана (без содержимого ID). */
internal data class OperationSummary(
    val id: String,
    val phase: DeleteOpPhase,
    val photos: Int,
    val remaining: Int,
    val hidden: Boolean,
)

internal data class DeviceFoldersUiState(
    val folders: List<DeviceFolder> = emptyList(),
    /** Статусы облака из Room; значок «в облаке» — по IN_CLOUD. */
    val cloudStates: Map<String, MediaCloudStatus> = emptyMap(),
    val selected: Set<String> = emptySet(),
    val busy: Boolean = false,
    val staging: StagingProgress? = null,
    val confirmation: DeleteConfirmation? = null,
    val unlockFor: UnlockReason? = null,
    val systemDelete: SystemDeleteRequest? = null,
    /** Операция, ожидающая ответа системы: навигация заблокирована, пока ответа нет. */
    val awaitingSystemOperationId: String? = null,
    val operations: List<OperationSummary> = emptyList(),
    val snackbar: String? = null,
)

/**
 * Операции над фото устройства из папок. Ожидающие удаления живут в [DeviceFoldersUiState] и в
 * durable-хранилище операций ([DeviceDeleteExecutor]); обычных изменяемых полей с операцией нет.
 * Облачные копии определяются по хешу непосредственно перед действием. Настройки автозагрузки не меняются.
 */
internal class DeviceFoldersViewModel(
    private val appContext: Context,
    private val cloud: CloudRepository,
    private val albums: AlbumRepository,
    private val queue: UploadQueueStore,
    private val mediaDao: MediaCloudStateDao,
    private val vaultIds: () -> Set<String>,
    private val executor: DeviceDeleteExecutor,
    private val scopeProvider: () -> CacheScope?,
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

    init {
        viewModelScope.launch {
            mediaDao.observeAll().collect { rows ->
                _state.update { it.copy(cloudStates = rows.associate { row -> row.mediaKey to row.status }) }
            }
        }
        viewModelScope.launch { recoverOperations() }
    }

    /** Перечитывает локальные папки; выбранные, которых больше нет, снимаются. */
    suspend fun reload(images: Boolean, videos: Boolean) {
        val items = withContext(Dispatchers.IO) { DeviceMediaStore.query(appContext, images, videos) }
        media = items
        val keys = items.map { it.mediaKey }.toSet()
        _state.update { it.copy(folders = deviceFolders(items), selected = it.selected.filter { key -> key in keys }.toSet()) }
    }

    fun requestPresence(item: DeviceMedia, force: Boolean) = presence.request(item, force)

    fun setSelection(keys: Set<String>) = _state.update { if (it.busy || it.staging != null) it else it.copy(selected = keys) }

    fun clearSelection() = _state.update { if (it.busy) it else it.copy(selected = emptySet()) }

    /** Ручная загрузка. Существующие в облаке копии не загружаются повторно. */
    fun uploadSelected() = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val toUpload = targets.filter { cloudByKey[it.mediaKey] == CloudCopy.Absent }
        val inCloud = targets.count { cloudByKey[it.mediaKey] is CloudCopy.Present }
        var queued = 0
        for ((index, item) in toUpload.withIndex()) {
            if (_state.value.staging?.cancelled == true) break
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
        if (queued > 0) UploadScheduler.enqueue(appContext)
        val unknown = targets.size - toUpload.size - inCloud
        val notQueued = unknown + (toUpload.size - queued)
        _state.update {
            it.copy(
                selected = emptySet(),
                snackbar = appContext.getString(R.string.device_upload_result, queued, inCloud, notQueued),
            )
        }
    }

    fun cancelStaging() = _state.update { it.copy(staging = it.staging?.copy(cancelled = true)) }

    /**
     * Добавление в альбом. Живая задача получает альбом; иначе существующая копия добавляется по
     * детерминированному ID; без копии ставится загрузка с альбомом. Скрытые копии требуют разблокировки.
     */
    fun addSelectedToAlbum(albumId: String) = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val hidden = cloudByKey.values.flatMap { (it as? CloudCopy.Present)?.fileIds.orEmpty() }.any { it in vaultIds() }
        if (hidden && !HiddenDeviceSession.unlocked) {
            _state.update { it.copy(unlockFor = UnlockReason.AddAlbum(albumId)) }
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

    /**
     * Подготовка удаления: состояния облака перепроверяются сейчас, подтверждение показывает число копий.
     * Если среди подтверждаемых копий есть скрытые, сначала нужна разблокировка.
     */
    fun prepareDelete(mode: DeviceDeleteMode) = runOperation {
        val targets = selectedMedia()
        val cloudByKey = resolveCloud(targets)
        val plan = planDeviceDelete(mode, targets.map { DeviceDeleteItem(it.mediaKey, cloudByKey[it.mediaKey] ?: CloudCopy.Unknown) })
        val hashes = targets.mapNotNull { item -> presence.hashOf(item)?.let { item.mediaKey to it } }.toMap()
        val confirmation = DeleteConfirmation(mode, plan, hashes)
        if (plan.hiddenCopyIds(vaultIds()).isNotEmpty() && !HiddenDeviceSession.unlocked) {
            _state.update { it.copy(unlockFor = UnlockReason.ConfirmDelete(confirmation)) }
            return@runOperation
        }
        _state.update { it.copy(confirmation = confirmation) }
    }

    fun cancelDelete() = _state.update { it.copy(confirmation = null) }

    /**
     * Подтверждение пользователем: операция сохраняется ДО системного диалога. Без входа в аккаунт
     * операция не создаётся.
     */
    fun confirmDelete() {
        val confirmation = _state.value.confirmation ?: return
        val scope = scopeProvider()
        if (scope == null) {
            _state.update { it.copy(confirmation = null, snackbar = appContext.getString(R.string.device_op_login)) }
            return
        }
        val uris = confirmation.plan.devicePhotos.mapNotNull { key -> media.firstOrNull { it.mediaKey == key }?.uri }
        if (uris.isEmpty()) {
            _state.update { it.copy(confirmation = null, snackbar = appContext.getString(R.string.device_delete_nothing)) }
            return
        }
        viewModelScope.launch {
            val operation = withContext(Dispatchers.IO) {
                executor.create(scope, confirmation.plan, confirmation.hashes, vaultIds())
            }
            _state.update {
                it.copy(
                    confirmation = null,
                    awaitingSystemOperationId = operation.id,
                    systemDelete = SystemDeleteRequest(operation.id, uris),
                )
            }
            refreshOperations()
        }
    }

    /** Системный запрос показан; повторно не запускаем. */
    fun systemDeleteRequested() = _state.update { it.copy(systemDelete = null) }

    /**
     * Ответ системы для операции [operationId]. Отказ снимает операцию, облако не трогается. Одобрение
     * фиксируется и выполняется в отдельной корутине, не занимая busy-гейт: одобрение нельзя потерять.
     */
    fun onSystemDeleteResult(operationId: String, approved: Boolean) {
        _state.update { it.copy(awaitingSystemOperationId = null, systemDelete = null) }
        viewModelScope.launch {
            if (!approved) {
                withContext(Dispatchers.IO) { executor.deny(operationId) }
                _state.update { it.copy(snackbar = appContext.getString(R.string.device_delete_cancelled)) }
                refreshOperations()
                return@launch
            }
            try {
                // Локальные фото, которые система только что удалила, берём до того, как операция снимется.
                val (devicePhotos, result) = withContext(Dispatchers.IO) {
                    val photos = executor.pendingFor(scopeProvider() ?: return@withContext emptySet<String>() to CloudRunResult.ScopeChanged)
                        .firstOrNull { it.id == operationId }?.devicePhotos.orEmpty().toSet()
                    executor.approve(operationId)
                    photos to executor.runCloud(operationId, allowHidden = true)
                }
                reportResult(result)
                media = media.filterNot { current -> current.mediaKey in devicePhotos }
                _state.update { it.copy(folders = deviceFolders(media), selected = emptySet()) }
                CloudLibraryChanges.publish(CloudLibraryChange.Changed)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(snackbar = e.message) }
            } finally {
                refreshOperations()
            }
        }
    }

    /** Продолжение незавершённой операции (после прерывания): скрытые цели — только после разблокировки. */
    fun resumeOperation(operationId: String) {
        viewModelScope.launch {
            val operation = withContext(Dispatchers.IO) { findOperation(operationId) } ?: return@launch
            if (operation.phase == DeleteOpPhase.AWAITING_SYSTEM) {
                _state.update { it.copy(snackbar = appContext.getString(R.string.device_op_awaiting)) }
                return@launch
            }
            if (operation.hasHiddenRemaining && !HiddenDeviceSession.unlocked) {
                _state.update { it.copy(unlockFor = UnlockReason.ResumeOperation(operationId)) }
                return@launch
            }
            runOperation { runCloudOperation(operationId) }
        }
    }

    /** Отмена операции, ещё не подтверждённой системой. Подтверждённые операции не отменяются. */
    fun discardOperation(operationId: String) {
        viewModelScope.launch {
            withContext(Dispatchers.IO) { executor.deny(operationId) }
            refreshOperations()
        }
    }

    /** Итог разблокировки Vault: при успехе продолжается отложенное действие, при отказе сбрасывается. */
    fun onUnlockResult(granted: Boolean) {
        val reason = _state.value.unlockFor
        _state.update { it.copy(unlockFor = null) }
        if (!granted || reason == null) return
        HiddenDeviceSession.unlocked = true
        when (reason) {
            is UnlockReason.ConfirmDelete -> _state.update { it.copy(confirmation = reason.confirmation) }
            is UnlockReason.AddAlbum -> addSelectedToAlbum(reason.albumId)
            is UnlockReason.ResumeOperation -> runOperation { runCloudOperation(reason.operationId) }
        }
    }

    /** Экран ушёл с переднего плана: разблокировка скрытых копий в памяти сбрасывается. */
    fun onScreenStopped() {
        HiddenDeviceSession.unlocked = false
    }

    fun snackbarShown() = _state.update { it.copy(snackbar = null) }

    private fun findOperation(operationId: String): DeleteOperation? =
        scopeProvider()?.let { scope -> executor.pendingFor(scope).firstOrNull { it.id == operationId } }

    /** Выполняет облачную часть подтверждённой операции (busy-гейт уже занят вызывающим). */
    private suspend fun runCloudOperation(operationId: String) {
        val result = withContext(Dispatchers.IO) { executor.runCloud(operationId, allowHidden = true) }
        reportResult(result)
    }

    private fun reportResult(result: CloudRunResult) {
        val message = when (result) {
            is CloudRunResult.Finished -> appContext.getString(
                R.string.device_op_result,
                result.deleted,
                result.reconciled,
                result.newCopies,
                result.failed,
            )
            is CloudRunResult.NeedsUnlock -> appContext.getString(R.string.device_op_needs_unlock)
            CloudRunResult.ScopeChanged -> appContext.getString(R.string.device_op_scope_changed)
            CloudRunResult.NotApproved -> appContext.getString(R.string.device_op_awaiting)
            CloudRunResult.NotFound -> null
        }
        _state.update { it.copy(snackbar = message) }
    }

    /**
     * Восстановление после перезапуска: незавершённые операции показываются; подтверждённые без скрытых
     * целей продолжаются автоматически. Неподтверждённые системой не выполняются — только явный выбор.
     */
    private suspend fun recoverOperations() {
        refreshOperations()
        val scope = scopeProvider() ?: return
        val approved = withContext(Dispatchers.IO) { executor.pendingFor(scope) }
            .filter { it.phase == DeleteOpPhase.APPROVED && !it.hasHiddenRemaining }
        for (operation in approved) {
            runOperation { runCloudOperation(operation.id) }
        }
        refreshOperations()
    }

    private suspend fun refreshOperations() {
        val scope = scopeProvider()
        val summaries = if (scope == null) {
            emptyList()
        } else {
            withContext(Dispatchers.IO) { executor.pendingFor(scope) }.map { op ->
                OperationSummary(
                    id = op.id,
                    phase = op.phase,
                    photos = op.photos.size,
                    remaining = op.remainingCount,
                    hidden = op.hasHiddenRemaining,
                )
            }
        }
        _state.update { it.copy(operations = summaries) }
    }

    private fun selectedMedia(): List<DeviceMedia> {
        val keys = _state.value.selected
        return media.filter { it.mediaKey in keys }
    }

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
                    mediaDao = com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase.get(app).mediaCloudStateDao(),
                    vaultIds = { app.vaultStore.items().map { it.fileId }.toSet() },
                    executor = app.deviceDeleteExecutor,
                    scopeProvider = app::currentOperationScope,
                ) as T
            }
        }
    }
}
