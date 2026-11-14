package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.HashPresence
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp
import com.barkfluff.BarkCloud.net.UploadPartHttpException
import com.barkfluff.BarkCloud.net.UploadSessionInfo
import com.barkfluff.BarkCloud.net.UploadSessionState
import io.grpc.StatusRuntimeException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.UUID

/**
 * Foreground-обработчик очереди загрузок (Upload 2.0). До [MAX_CONCURRENT_JOBS] файлов
 * грузятся параллельно, части каждого файла — строго последовательно. Восстановление
 * опирается на серверный список частей (`ResumeUploadSession`), а не на локальный
 * прогресс: после перезапуска процесса докачиваются только отсутствующие части.
 */
class UploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val runStart = System.currentTimeMillis()
        val runToken = UploadNotificationDismissal.beginRun()
        val refreshPublisher = UploadRefreshPublisher(
            mainContext = Dispatchers.Main.immediate,
            isDismissed = { UploadNotificationDismissal.isDismissed(runToken) },
            isRegistered = { UploadNotification.isForegroundRegistered(applicationContext, runToken) },
            notify = { tally -> UploadNotification.update(applicationContext, tally, runToken) },
        )
        // Итоговое уведомление прежних версий больше не нужно: снимаем оставшееся.
        applicationContext.getSystemService(NotificationManager::class.java)?.cancel(UploadNotification.FINISHED_ID)
        val queue = UploadQueueStore(applicationContext)
        queue.initialize()

        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.failure()
        val autoUploadSettings = AutoUploadSettings(applicationContext)

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            val albums = AlbumRepository(grpc)
            val mediaDao = BarkCloudDatabase.get(applicationContext).mediaCloudStateDao()

            // Older app versions staged the whole discovered backup queue at once.
            queue.releaseExcessBackupStages(MAX_CONCURRENT_JOBS)

            // Задачи, отменённые мимо воркера: освобождаем резерв квоты на сервере.
            queue.cancelledWithSessions().forEach { cancelled ->
                runCatching { transfer.cancelUploadSession(requireNotNull(cancelled.sessionId)) }
                queue.clearSession(cancelled.id)
            }

            // Счёт по всей eligible-очереди, а не только по захваченным задачам. Пустой проход без чипа.
            val progress = UploadRunProgress(queue, runStart) {
                shouldUploadBackup(autoUploadSettings.policy, isUnmeteredNetwork())
            }
            val initialTally = progress.tally()
            if (initialTally.remaining == 0) return Result.success()

            val pending = mutableListOf<PendingRetry>()
            val stopClaiming = AtomicBoolean(false)
            val stagingBudget = BackupStagingBudget(queue.stagedBackupCount())
            // Единственный setForeground на проход. WorkManager ставит в очередь две асинхронные команды сервиса,
            // публикация не подтверждается; дальше обновления идут напрямую и только при видимой регистрации.
            // С Android 12 запуск из фона (discovery, MediaStore, продолжение очереди) может быть запрещён:
            // тогда проход идёт без foreground, а не падает целиком, как раньше до открытия приложения.
            try {
                setForeground(foregroundInfo(UploadNotification.foreground(applicationContext, initialTally, runToken)))
            } catch (_: IllegalStateException) {
                // ForegroundServiceStartNotAllowedException — подкласс IllegalStateException.
            }

            coroutineScope {
                val notificationConsumer = launch {
                    consumeUploadNotifications(notifyChannel) {
                        refreshPublisher.publish { progress.tally().takeIf { it.total > 0 } }
                    }
                }
                // Задачи могут добавиться извне (discovery), поэтому счёт пересчитывается и по таймеру.
                val periodicRefresh = launch {
                    while (true) {
                        delay(NOTIFICATION_REFRESH_MILLIS)
                        notifyChannel.trySend(Unit)
                    }
                }
                try {
                    UploadQueueDrain.run(
                        source = queue,
                        pageSize = MAX_CONCURRENT_JOBS,
                        shouldStop = stopClaiming::get,
                        shouldProcess = { job ->
                            shouldProcessUploadJob(job, autoUploadSettings.policy, isUnmeteredNetwork())
                        },
                    ) { job ->
                        val hadStagedFile = job.source == UploadSource.BACKUP && job.stagedFilePath != null
                        val reservedStage = AtomicBoolean(false)
                        try {
                            process(
                                job,
                                queue,
                                transfer,
                                cloud,
                                albums,
                                mediaDao,
                                stagingBudget,
                            ) { reservedStage.set(true) }
                        } catch (deferred: BackupStageDeferredException) {
                            // Staging отложен бюджетом: задача остаётся в очереди.
                        } catch (paused: JobPausedException) {
                            // Пауза не считается ошибкой и не попадает в счёт.
                        } catch (cancelled: JobCancelledException) {
                            runCatching { job.sessionId?.let { transfer.cancelUploadSession(it) } }
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Throwable) {
                            handleFailure(job, error, queue, transfer, mediaDao, pending, stopClaiming)
                        } finally {
                            if (job.source == UploadSource.BACKUP && (hadStagedFile || reservedStage.get())) {
                                if (queue.byId(job.id)?.stagedFilePath == null) stagingBudget.release()
                            }
                            // Итог берётся из статуса в БД, а не из исхода `try`: complete мог не сработать.
                            progress.noteEnded(job.id)
                            notifyChannel.trySend(Unit)
                        }
                    }
                } finally {
                    periodicRefresh.cancel()
                    notifyChannel.close()
                    notificationConsumer.cancelAndJoin()
                }
            }

            val retryable = pending.filter { it.retryable }
            if (retryable.isNotEmpty() && runAttemptCount < MAX_RETRY_ATTEMPTS) {
                Result.retry()
            } else {
                retryable.forEach { pendingRetry ->
                    finalizeExhausted(pendingRetry, queue, mediaDao)
                    progress.noteEnded(pendingRetry.jobId)
                }
                val activeJobs = if (shouldUploadBackup(autoUploadSettings.policy, isUnmeteredNetwork())) {
                    queue.activeCount()
                } else {
                    queue.activeNonBackupCount()
                }
                if (shouldScheduleUploadContinuation(
                        stopClaiming = stopClaiming.get(),
                        activeJobs = activeJobs,
                        stagedBackups = queue.stagedBackupCount(),
                        maxStagedBackups = MAX_CONCURRENT_JOBS,
                    )
                ) {
                    UploadScheduler.enqueue(applicationContext, userInitiated = false)
                }
                Result.success()
            }
        } finally {
            grpc.shutdown()
        }
    }

    // MARK: State machine одного файла

    private suspend fun process(
        initial: UploadJob,
        queue: UploadQueueStore,
        transfer: FileTransferService,
        cloud: CloudRepository,
        albums: AlbumRepository,
        mediaDao: MediaCloudStateDao,
        stagingBudget: BackupStagingBudget,
        onStageReserved: () -> Unit,
    ) {
        var job = initial
        val backupSettings = if (job.source == UploadSource.BACKUP) AutoUploadSettings(applicationContext) else null
        if (backupSettings != null) {
            ensureActive(job.id, queue)
            if (!backupSettings.enabled) {
                queue.pauseBackup()
                throw JobPausedException()
            }
        }

        // A ready upload needs only its saved file ID and destination; source staging is irrelevant.
        if (job.isAttachOnlyPhase()) {
            attachReady(job, queue, cloud, albums, mediaDao)
            return
        }

        if (job.needsBackupStaging()) {
            if (job.stagedFilePath == null && !stagingBudget.reserve()) throw BackupStageDeferredException()
            if (job.stagedFilePath == null) onStageReserved()
            val prepared = queue.prepareBackup(job.id, mediaDao) { media ->
                ensureBackupActive(job.id, queue, requireNotNull(backupSettings), media)
            }
            if (prepared == null) {
                when (queue.byId(job.id)?.phase) {
                    UploadPhase.CANCELLED -> throw JobCancelledException()
                    UploadPhase.PAUSED -> throw JobPausedException()
                    else -> throw BackupStageDeferredException()
                }
            }
            prepared.staleSessionId?.let { staleSession -> runCatching { transfer.cancelUploadSession(staleSession) } }
            job = prepared.job
        }

        val staged = job.stagedFilePath?.let(::File) ?: throw UploadTerminalException("No staged file for ${job.id}")
        if (!staged.exists()) throw FileNotFoundException(staged.path)
        if (job.bytesTotal <= 0L) throw UploadTerminalException(applicationContext.getString(R.string.upload_error_empty_file))

        // 1. Manual/share uploads still hash their eager staged file here.
        if (job.mediaHash == null) {
            queue.setPhase(job.id, UploadPhase.HASHING)
            val hashed = UploadFileHasher.hashAndSize(staged)
                ?: throw UploadTerminalException("Не удалось прочитать файл")
            if (job.sessionId == null && hashed.second != job.bytesTotal) {
                throw UploadTerminalException("Размер файла изменился")
            }
            queue.setHash(job.id, hashed.first, hashed.second)
            job = requireNotNull(queue.byId(job.id))
        }

        // Backups retain the existing cloud-hash dedupe, now after lazy staging.
        if (job.source == UploadSource.BACKUP && job.sessionId == null && job.preparedFileId == null) {
            val hash = requireNotNull(job.mediaHash)
            val presence = cloud.checkFileHashesDetailed(listOf(hash))[hash]
            if (presence?.exists == true) {
                completeDeduplicated(job, hash, presence, queue, albums, mediaDao)
                return
            }
        }

        // 3. Сессия и дальше по серверному состоянию.
        var info = obtainSession(job, queue, transfer)
        while (true) {
            when (info.state) {
                UploadSessionState.READY -> {
                    attachReady(job.copy(preparedFileId = info.fileId), queue, cloud, albums, mediaDao)
                    return
                }
                UploadSessionState.PROCESSING -> info = pollProcessing(job, info, queue, transfer)
                UploadSessionState.UPLOADING -> info = uploadPartsAndComplete(job, info, staged, queue, transfer, mediaDao)
                else -> throw UploadTerminalException(info.errorMessage ?: "Сессия ${info.state}")
            }
        }
    }

    private suspend fun ensureBackupActive(
        id: String,
        queue: UploadQueueStore,
        settings: AutoUploadSettings,
        media: DeviceMedia,
    ) {
        ensureActive(id, queue)
        if (!settings.enabled) {
            queue.pauseBackup()
            throw JobPausedException()
        }
        if (!hasMediaPermission(media.isVideo)) throw BackupPermissionUnavailableException()
        if (!settings.includesFolder(media.folderKey)) {
            queue.pauseBackupJob(id)
            throw JobPausedException()
        }
    }

    private fun hasMediaPermission(isVideo: Boolean): Boolean {
        val permission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (isVideo) android.Manifest.permission.READ_MEDIA_VIDEO else android.Manifest.permission.READ_MEDIA_IMAGES
        } else {
            android.Manifest.permission.READ_EXTERNAL_STORAGE
        }
        if (androidx.core.content.ContextCompat.checkSelfPermission(applicationContext, permission) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
            return true
        }
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE &&
            androidx.core.content.ContextCompat.checkSelfPermission(
                applicationContext,
                android.Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED,
            ) == android.content.pm.PackageManager.PERMISSION_GRANTED
    }

    private fun isUnmeteredNetwork(): Boolean {
        val connectivity = applicationContext.getSystemService(ConnectivityManager::class.java)
        val network = connectivity?.activeNetwork ?: return false
        val capabilities = connectivity.getNetworkCapabilities(network) ?: return false
        return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }

    private suspend fun obtainSession(job: UploadJob, queue: UploadQueueStore, transfer: FileTransferService): UploadSessionInfo {
        if (job.sessionId == null) {
            return createSession(job, job.idempotencyKey ?: UUID.randomUUID().toString(), queue, transfer)
        }
        val fetched = if (job.phase == UploadPhase.UPLOADING) {
            // Нужен upload-token + авторитетный список частей.
            transfer.resumeUploadSession(job.sessionId)
        } else {
            // COMPLETING: complete мог дойти, но ответ потеряться — сверяемся состоянием.
            val state = transfer.getUploadSession(job.sessionId)
            if (state.state == UploadSessionState.UPLOADING) transfer.resumeUploadSession(job.sessionId) else state
        }
        return if (fetched.state == UploadSessionState.FAILED || fetched.state == UploadSessionState.CANCELLED || fetched.state == UploadSessionState.EXPIRED) {
            // Сессия мертва: новая с новым ключом — старый связан с мёртвой в (OwnerId, Key).
            createSession(job, UUID.randomUUID().toString(), queue, transfer)
        } else {
            fetched
        }
    }

    private suspend fun createSession(
        job: UploadJob,
        idempotencyKey: String,
        queue: UploadQueueStore,
        transfer: FileTransferService,
    ): UploadSessionInfo {
        if (queue.prepareUploadSession(job.id, idempotencyKey, replaceExisting = job.sessionId != null) == 0) {
            if (queue.byId(job.id)?.phase == UploadPhase.PAUSED) throw JobPausedException()
            throw JobCancelledException()
        }
        val info = transfer.createUploadSession(
            idempotencyKey = idempotencyKey,
            fileName = job.fileName,
            fileSize = job.bytesTotal,
            contentType = job.mimeType ?: "application/octet-stream",
            sha256 = requireNotNull(job.mediaHash),
        )
        if (queue.bindSession(job.id, info.sessionId, idempotencyKey, info.fileId, info.partSize) == 0) {
            runCatching { transfer.cancelUploadSession(info.sessionId) }
            if (queue.byId(job.id)?.phase == UploadPhase.PAUSED) throw JobPausedException()
            throw JobCancelledException()
        }
        return info
    }

    private suspend fun uploadPartsAndComplete(
        job: UploadJob,
        session: UploadSessionInfo,
        staged: File,
        queue: UploadQueueStore,
        transfer: FileTransferService,
        mediaDao: MediaCloudStateDao,
    ): UploadSessionInfo {
        if (session.partSize <= 0L) throw UploadTerminalException("Сервер вернул некорректный размер части")
        if (session.fileSize != job.bytesTotal) throw UploadTerminalException("Размер файла изменился с момента создания сессии")

        queue.setPhase(job.id, UploadPhase.UPLOADING)
        job.mediaKey?.let { key ->
            mediaDao.byKey(key)?.let { mediaDao.upsert(it.copy(status = MediaCloudStatus.UPLOADING)) }
        }

        var active = session
        var token = session.uploadToken
            ?: transfer.resumeUploadSession(session.sessionId).also { resumed ->
                if (resumed.state != UploadSessionState.UPLOADING) return resumed
                active = resumed
            }.uploadToken
            ?: throw UploadTerminalException("Сервер не выдал upload-token")

        uploadMissingParts(job, active, token, staged, queue, transfer)

        queue.setPhase(job.id, UploadPhase.COMPLETING)
        try {
            return transfer.completeUploadSession(active.sessionId)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            val incomplete = (error as? StatusRuntimeException)?.errorCode() == UploadErrorCodes.UPLOAD_PARTS_INCOMPLETE
            val reconciled = runCatching { transfer.getUploadSession(active.sessionId) }.getOrNull()
            when {
                // Complete фактически сработал (ответ потерялся) либо сессия уже терминальная.
                reconciled != null && reconciled.state != UploadSessionState.UPLOADING -> return reconciled
                !incomplete -> throw error
            }
            // Один recovery-цикл: resume → дослать отсутствующие части → повторить Complete.
            val resumed = transfer.resumeUploadSession(active.sessionId)
            if (resumed.state != UploadSessionState.UPLOADING) return resumed
            uploadMissingParts(job, resumed, requireNotNull(resumed.uploadToken), staged, queue, transfer)
            return transfer.completeUploadSession(active.sessionId)
        }
    }

    private suspend fun uploadMissingParts(
        job: UploadJob,
        session: UploadSessionInfo,
        token: String,
        staged: File,
        queue: UploadQueueStore,
        transfer: FileTransferService,
    ) {
        val total = job.bytesTotal
        val partSize = session.partSize
        var confirmedBytes = UploadPartMath.confirmedBytes(session.uploadedParts, total, partSize)
        queue.setProgress(job.id, confirmedBytes, total, UploadPhase.UPLOADING)

        for (partNumber in UploadPartMath.missingPartNumbers(session.uploadedParts, total, partSize)) {
            ensureActive(job.id, queue)
            val offset = UploadPartMath.partOffset(partNumber, partSize)
            val size = UploadPartMath.partLength(partNumber, total, partSize)
            uploadPartWithRetry(transfer, session.sessionId, partNumber, token, staged, offset, size, total)
            confirmedBytes += size
            queue.setProgress(job.id, confirmedBytes, total, UploadPhase.UPLOADING)
        }
    }

    private suspend fun uploadPartWithRetry(
        transfer: FileTransferService,
        sessionId: String,
        partNumber: Int,
        token: String,
        staged: File,
        offset: Long,
        size: Long,
        total: Long,
    ) {
        var attempt = 0
        while (true) {
            try {
                transfer.uploadSessionPart(sessionId, partNumber, token, staged, offset, size, total)
                return
            } catch (error: UploadPartHttpException) {
                if (!error.retryable || attempt >= MAX_PART_ATTEMPTS - 1) throw error
            } catch (error: IOException) {
                if (attempt >= MAX_PART_ATTEMPTS - 1) throw error
            }
            delay(PART_RETRY_BASE_MILLIS shl attempt)
            attempt++
        }
    }

    private suspend fun pollProcessing(
        job: UploadJob,
        info: UploadSessionInfo,
        queue: UploadQueueStore,
        transfer: FileTransferService,
    ): UploadSessionInfo {
        queue.setPhase(job.id, UploadPhase.PROCESSING)
        queue.setProgress(job.id, job.bytesTotal, job.bytesTotal, UploadPhase.PROCESSING)
        while (true) {
            ensureActive(job.id, queue)
            delay(PROCESSING_POLL_MILLIS)
            val state = transfer.getUploadSession(info.sessionId)
            if (state.state != UploadSessionState.PROCESSING) return state
        }
    }

    private suspend fun attachReady(
        job: UploadJob,
        queue: UploadQueueStore,
        cloud: CloudRepository,
        albums: AlbumRepository,
        mediaDao: MediaCloudStateDao,
    ) {
        val fileId = requireNotNull(job.preparedFileId) { "ready without fileId for ${job.id}" }
        val isUploadRetry = job.phase == UploadPhase.ATTACHING || job.phase == UploadPhase.UPLOADED_NOT_ATTACHED
        queue.setPhase(job.id, UploadPhase.ATTACHING)
        try {
            when (job.destination) {
                UploadDestination.SYSTEM_BY_MEDIA_KIND ->
                    cloud.attachFile(fileId, "", job.fileName, routeByMediaKind = true, uploadSessionId = job.sessionId.orEmpty(), isUploadRetry = isUploadRetry)
                UploadDestination.DIRECTORY ->
                    cloud.attachFile(fileId, requireNotNull(job.directoryId), job.fileName, uploadSessionId = job.sessionId.orEmpty(), isUploadRetry = isUploadRetry)
            }
        } catch (error: StatusRuntimeException) {
            if (error.errorCode() != UploadErrorCodes.FILE_ALREADY_ATTACHED) throw AttachFailedException(error)
        }
        // Фаза ATTACHING уже выставлена: запросы альбома к этой задаче больше не принимаются, поэтому
        // читаем итоговый albumId из БД и не теряем его.
        queue.byId(job.id)?.albumId?.let { albums.addItems(it, listOf(fileId)) }
        queue.complete(job)
        job.mediaKey?.let { key ->
            mediaDao.byKey(key)?.let { mediaDao.upsert(it.copy(status = MediaCloudStatus.IN_CLOUD, cloudFileId = fileId)) }
        }
    }

    /**
     * Файл уже есть в облаке: загрузка не нужна. Ожидающий альбом получает детерминированный
     * (наименьший) пригодный ID. Без пригодного ID при альбоме задача ломается с понятным текстом,
     * а не завершается молча и не загружает повторно.
     */
    private suspend fun completeDeduplicated(
        job: UploadJob,
        hash: String,
        presence: HashPresence,
        queue: UploadQueueStore,
        albums: AlbumRepository,
        mediaDao: MediaCloudStateDao,
    ) {
        val fileId = presence.fileIds.minOrNull()
        when (
            val outcome = completeDeduplicatedWithAlbum(
                fileId = fileId,
                read = { queue.dedupRead(job.id) },
                addToAlbum = { album, id -> albums.addItems(album, listOf(id)) },
                completeIfAlbum = { album -> queue.completeDeduplicated(job.id, album) },
            )
        ) {
            is DedupOutcome.Completed -> job.mediaKey?.let { key ->
                mediaDao.byKey(key)?.let { mediaDao.upsert(it.copy(hash = hash, status = MediaCloudStatus.IN_CLOUD, cloudFileId = fileId)) }
            }
            // Отменена/приостановлена/завершена между чтением и CAS: статус облака не публикуем.
            DedupOutcome.Inactive -> Unit
            DedupOutcome.MissingFileId -> throw UploadTerminalException(
                "Сервер не вернул ID существующей копии для альбома. Обновите сервер и повторите",
            )
            DedupOutcome.Exhausted -> throw IllegalStateException(
                "Альбом задачи менялся слишком часто; повтор выполнится позже",
            )
        }
    }

    // MARK: Ошибки и retry

    private suspend fun handleFailure(
        job: UploadJob,
        error: Throwable,
        queue: UploadQueueStore,
        transfer: FileTransferService,
        mediaDao: MediaCloudStateDao,
        pending: MutableList<PendingRetry>,
        stopClaiming: AtomicBoolean,
    ) {
        val current = queue.byId(job.id)
        if (current?.phase == UploadPhase.CANCELLED) {
            runCatching { current.sessionId?.let { transfer.cancelUploadSession(it) } }
            return
        }
        if (current?.phase == UploadPhase.PAUSED) return
        val activeJob = current ?: job
        if (error is AttachFailedException) {
            val cause = error.cause ?: error
            val disposition = UploadFailurePolicy.classify(
                cause,
                job.source,
                explicitTerminal = cause is UploadTerminalException,
                permissionUnavailable = cause is BackupPermissionUnavailableException,
            )
            val message = failureMessage(cause, disposition)
            when (disposition) {
                UploadFailureDisposition.ITEM_TERMINAL -> {
                    queue.setPhase(job.id, UploadPhase.UPLOADED_NOT_ATTACHED, message)
                    queue.dropStaging(job.id)
                }
                else -> {
                    queue.setPhase(job.id, UploadPhase.ATTACHING, message)
                    synchronized(pending) { pending.add(PendingRetry(job.id, retryable = true)) }
                    stopClaiming.set(true)
                }
            }
            return
        }

        val disposition = UploadFailurePolicy.classify(
            error,
            job.source,
            explicitTerminal = error is UploadTerminalException,
            permissionUnavailable = error is BackupPermissionUnavailableException,
        )
        val message = failureMessage(error, disposition)
        when (disposition) {
            UploadFailureDisposition.PERMISSION_STOP_CLAIMING -> {
                queue.setPhase(job.id, UploadPhase.QUEUED, message)
                stopClaiming.set(true)
            }
            UploadFailureDisposition.QUOTA_STOP_CLAIMING -> {
                queue.failAfterQuota(job.id, message)
                markMediaError(job, mediaDao)
                stopClaiming.set(true)
            }
            UploadFailureDisposition.STORAGE_STOP_CLAIMING -> {
                queue.setPhase(job.id, UploadPhase.QUEUED, message)
                synchronized(pending) { pending.add(PendingRetry(job.id, retryable = true)) }
                stopClaiming.set(true)
            }
            UploadFailureDisposition.ITEM_TERMINAL -> {
                if (job.source == UploadSource.BACKUP) {
                    runCatching { activeJob.sessionId?.let { transfer.cancelUploadSession(it) } }
                    queue.failBackupAndDropStage(job.id, message)
                } else {
                    queue.setPhase(job.id, UploadPhase.FAILED, message)
                }
                markMediaError(job, mediaDao)
            }
            UploadFailureDisposition.RETRY_AND_STOP_CLAIMING -> {
                queue.setPhase(job.id, resumePhase(activeJob), message)
                synchronized(pending) { pending.add(PendingRetry(job.id, retryable = true)) }
                stopClaiming.set(true)
            }
        }
    }

    /** WorkManager-попытки исчерпаны: отложенные ретраи фиксируем терминально. */
    private suspend fun finalizeExhausted(pending: PendingRetry, queue: UploadQueueStore, mediaDao: MediaCloudStateDao) {
        val job = queue.byId(pending.jobId) ?: return
        when (job.phase) {
            UploadPhase.ATTACHING -> {
                queue.setPhase(job.id, UploadPhase.UPLOADED_NOT_ATTACHED, job.errorMessage)
                queue.dropStaging(job.id)
            }
            UploadPhase.QUEUED, UploadPhase.HASHING, UploadPhase.CREATING_SESSION,
            UploadPhase.UPLOADING, UploadPhase.COMPLETING, UploadPhase.PROCESSING -> {
                if (job.source == UploadSource.BACKUP) queue.failBackupAndDropStage(job.id, job.errorMessage)
                else queue.setPhase(job.id, UploadPhase.FAILED, job.errorMessage)
                markMediaError(job, mediaDao)
            }
            else -> Unit
        }
    }

    private suspend fun markMediaError(job: UploadJob, mediaDao: MediaCloudStateDao) {
        job.mediaKey?.let { key ->
            mediaDao.byKey(key)?.let { state ->
                mediaDao.upsert(state.copy(status = MediaCloudStatus.ERROR))
            }
        }
    }

    private fun failureMessage(error: Throwable, disposition: UploadFailureDisposition): String =
        if (disposition == UploadFailureDisposition.QUOTA_STOP_CLAIMING) {
            applicationContext.getString(R.string.upload_error_quota_exceeded)
        } else {
            error.message ?: error::class.java.simpleName
        }

    private fun resumePhase(job: UploadJob): UploadPhase = when {
        job.sessionId == null -> UploadPhase.QUEUED
        job.phase == UploadPhase.PROCESSING -> UploadPhase.PROCESSING
        else -> UploadPhase.UPLOADING
    }

    private suspend fun ensureActive(id: String, queue: UploadQueueStore) {
        when (queue.byId(id)?.phase) {
            null,
            UploadPhase.CANCELLED -> throw JobCancelledException()
            UploadPhase.PAUSED -> throw JobPausedException()
            else -> Unit
        }
    }

    private fun foregroundInfo(notification: Notification): ForegroundInfo =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(UploadNotification.ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(UploadNotification.ID, notification)
        }

    // MARK: Вспомогательные типы

    private class JobCancelledException : RuntimeException()
    private class JobPausedException : RuntimeException()
    private class BackupStageDeferredException : RuntimeException()
    private class BackupPermissionUnavailableException : RuntimeException()
    private class UploadTerminalException(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

    /** Байты уже на сервере; упало только post-ready действие. */
    private class AttachFailedException(cause: Throwable) : RuntimeException(cause)

    private class PendingRetry(val jobId: String, val retryable: Boolean)

    private val notifyChannel = Channel<Unit>(Channel.CONFLATED)

    private companion object {
        const val MAX_CONCURRENT_JOBS = 4
        const val MAX_RETRY_ATTEMPTS = 5
        const val MAX_PART_ATTEMPTS = 3
        const val PART_RETRY_BASE_MILLIS = 500L
        const val PROCESSING_POLL_MILLIS = 2_000L
        const val NOTIFICATION_REFRESH_MILLIS = 5_000L
    }
}

internal suspend fun consumeUploadNotifications(channel: kotlinx.coroutines.channels.ReceiveChannel<Unit>, update: suspend () -> Unit) {
    for (ignored in channel) update()
}

internal class BackupStagingBudget(initialCount: Int, private val maximum: Int = 4) {
    private var held = initialCount.coerceAtLeast(0)

    @Synchronized
    fun reserve(): Boolean {
        if (held >= maximum) return false
        held++
        return true
    }

    @Synchronized
    fun release() {
        if (held > 0) held--
    }

    @Synchronized
    fun heldCount(): Int = held
}
