package com.barkfluff.BarkCloud.data.upload

import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.SystemClock
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
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
import io.grpc.Status
import io.grpc.StatusRuntimeException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException
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
        val queue = UploadQueueStore(applicationContext)
        queue.initialize()

        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.failure()

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, globalParam, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            val albums = AlbumRepository(grpc)
            val mediaDao = BarkCloudDatabase.get(applicationContext).mediaCloudStateDao()

            // Задачи, отменённые мимо воркера: освобождаем резерв квоты на сервере.
            queue.cancelledWithSessions().forEach { cancelled ->
                runCatching { transfer.cancelUploadSession(requireNotNull(cancelled.sessionId)) }
                queue.clearSession(cancelled.id)
            }

            if (queue.activeJobs().isEmpty()) return Result.success()

            val tracker = ProgressTracker { notifyChannel.trySend(Unit) }
            val pending = mutableListOf<PendingRetry>()
            setForeground(progressInfo(tracker.snapshot()))

            coroutineScope {
                launch {
                    for (ignored in notifyChannel) {
                        runCatching { setForeground(progressInfo(tracker.snapshot())) }
                    }
                }
                val semaphore = Semaphore(MAX_CONCURRENT_JOBS)
                val claimed = mutableSetOf<String>()
                while (true) {
                    semaphore.acquire()
                    val job = queue.activeJobs().firstOrNull { it.id !in claimed }
                    if (job == null) {
                        semaphore.release()
                        break
                    }
                    claimed += job.id
                    tracker.register(job.id, job.bytesTotal, job.fileName)
                    launch {
                        try {
                            process(job, queue, transfer, cloud, albums, mediaDao, tracker)
                            tracker.finish(job.id)
                        } catch (paused: JobPausedException) {
                            tracker.remove(job.id)
                        } catch (cancelled: JobCancelledException) {
                            tracker.remove(job.id)
                            runCatching { job.sessionId?.let { transfer.cancelUploadSession(it) } }
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Throwable) {
                            tracker.finish(job.id)
                            handleFailure(job, error, queue, transfer, mediaDao, pending)
                        } finally {
                            semaphore.release()
                        }
                    }
                }
            }

            applicationContext.getSystemService(NotificationManager::class.java)
                .notify(UploadNotification.ID, UploadNotification.finished(applicationContext, tracker.doneTotal))

            val retryable = pending.filter { it.retryable }
            if (retryable.isNotEmpty() && runAttemptCount < MAX_RETRY_ATTEMPTS) {
                Result.retry()
            } else {
                retryable.forEach { finalizeExhausted(it, queue, mediaDao) }
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
        tracker: ProgressTracker,
    ) {
        var job = initial
        val staged = job.stagedFilePath?.let(::File) ?: throw UploadTerminalException("No staged file for ${job.id}")
        if (!staged.exists()) throw FileNotFoundException(staged.path)
        if (job.bytesTotal <= 0L) throw UploadTerminalException(applicationContext.getString(R.string.upload_error_empty_file))

        // 1. SHA-256 оригинала (backup приносит готовый хеш).
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

        // 2. Байты уже на сервере — только привязка.
        if (job.phase == UploadPhase.ATTACHING || job.phase == UploadPhase.UPLOADED_NOT_ATTACHED) {
            attachReady(job, queue, cloud, albums, mediaDao)
            return
        }

        // 3. Сессия и дальше по серверному состоянию.
        var info = obtainSession(job, queue, transfer)
        while (true) {
            when (info.state) {
                UploadSessionState.READY -> {
                    attachReady(job.copy(preparedFileId = info.fileId), queue, cloud, albums, mediaDao)
                    return
                }
                UploadSessionState.PROCESSING -> info = pollProcessing(job, info, queue, transfer, tracker)
                UploadSessionState.UPLOADING -> info = uploadPartsAndComplete(job, info, staged, queue, transfer, tracker, mediaDao)
                else -> throw UploadTerminalException(info.errorMessage ?: "Сессия ${info.state}")
            }
        }
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
        queue.setPhase(job.id, UploadPhase.CREATING_SESSION)
        val info = try {
            transfer.createUploadSession(
                idempotencyKey = idempotencyKey,
                fileName = job.fileName,
                fileSize = job.bytesTotal,
                contentType = job.mimeType ?: "application/octet-stream",
                sha256 = requireNotNull(job.mediaHash),
            )
        } catch (error: StatusRuntimeException) {
            if (error.errorCode() == UploadErrorCodes.QUOTA_EXCEEDED) {
                throw UploadTerminalException(applicationContext.getString(R.string.upload_error_quota_exceeded), error)
            }
            throw error
        }
        queue.bindSession(job.id, info.sessionId, idempotencyKey, info.fileId, info.partSize)
        return info
    }

    private suspend fun uploadPartsAndComplete(
        job: UploadJob,
        session: UploadSessionInfo,
        staged: File,
        queue: UploadQueueStore,
        transfer: FileTransferService,
        tracker: ProgressTracker,
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

        uploadMissingParts(job, active, token, staged, queue, transfer, tracker)

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
            uploadMissingParts(job, resumed, requireNotNull(resumed.uploadToken), staged, queue, transfer, tracker)
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
        tracker: ProgressTracker,
    ) {
        val total = job.bytesTotal
        val partSize = session.partSize
        var confirmedBytes = UploadPartMath.confirmedBytes(session.uploadedParts, total, partSize)
        queue.setProgress(job.id, confirmedBytes, total, UploadPhase.UPLOADING)
        tracker.progress(job.id, confirmedBytes)

        for (partNumber in UploadPartMath.missingPartNumbers(session.uploadedParts, total, partSize)) {
            ensureActive(job.id, queue)
            val offset = UploadPartMath.partOffset(partNumber, partSize)
            val size = UploadPartMath.partLength(partNumber, total, partSize)
            uploadPartWithRetry(transfer, session.sessionId, partNumber, token, staged, offset, size, total) { sentInPart ->
                tracker.progress(job.id, confirmedBytes + sentInPart)
            }
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
        onProgress: (Long) -> Unit,
    ) {
        var attempt = 0
        while (true) {
            try {
                transfer.uploadSessionPart(sessionId, partNumber, token, staged, offset, size, total, onProgress)
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
        tracker: ProgressTracker,
    ): UploadSessionInfo {
        queue.setPhase(job.id, UploadPhase.PROCESSING)
        queue.setProgress(job.id, job.bytesTotal, job.bytesTotal, UploadPhase.PROCESSING)
        tracker.progress(job.id, job.bytesTotal)
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
        job.albumId?.let { albums.addItems(it, listOf(fileId)) }
        queue.complete(job)
        job.mediaKey?.let { key ->
            mediaDao.byKey(key)?.let { mediaDao.upsert(it.copy(status = MediaCloudStatus.IN_CLOUD, cloudFileId = fileId)) }
        }
        if (job.source == UploadSource.BACKUP) {
            AutoUploadScheduler.runOnce(applicationContext, AutoUploadSettings(applicationContext).policy)
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
    ) {
        val current = queue.byId(job.id)
        if (current?.phase == UploadPhase.CANCELLED) {
            runCatching { current.sessionId?.let { transfer.cancelUploadSession(it) } }
            return
        }
        val message = error.message ?: error::class.java.simpleName
        when {
            error is AttachFailedException -> if (shouldRetry(error.cause)) {
                queue.setPhase(job.id, UploadPhase.ATTACHING, message)
                synchronized(pending) { pending.add(PendingRetry(job.id, retryable = true)) }
            } else {
                queue.setPhase(job.id, UploadPhase.UPLOADED_NOT_ATTACHED, message)
                queue.dropStaging(job.id)
            }
            error is UploadTerminalException -> {
                queue.setPhase(job.id, UploadPhase.FAILED, message)
                markMediaError(job, mediaDao)
            }
            shouldRetry(error) -> {
                queue.setPhase(job.id, resumePhase(job), message)
                synchronized(pending) { pending.add(PendingRetry(job.id, retryable = true)) }
            }
            else -> {
                queue.setPhase(job.id, UploadPhase.FAILED, message)
                markMediaError(job, mediaDao)
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
                queue.setPhase(job.id, UploadPhase.FAILED, job.errorMessage)
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

    private fun resumePhase(job: UploadJob): UploadPhase = when {
        job.sessionId == null -> UploadPhase.QUEUED
        job.phase == UploadPhase.PROCESSING -> UploadPhase.PROCESSING
        else -> UploadPhase.UPLOADING
    }

    private fun shouldRetry(error: Throwable?): Boolean = when (error) {
        null -> false
        is FileNotFoundException -> false
        is UploadTerminalException -> false
        is UploadPartHttpException -> error.retryable
        is StatusRuntimeException -> error.status.code in setOf(
            Status.Code.UNAVAILABLE,
            Status.Code.DEADLINE_EXCEEDED,
            Status.Code.RESOURCE_EXHAUSTED,
        )
        else -> true
    }

    private suspend fun ensureActive(id: String, queue: UploadQueueStore) {
        when (queue.byId(id)?.phase) {
            UploadPhase.CANCELLED -> throw JobCancelledException()
            UploadPhase.PAUSED -> throw JobPausedException()
            else -> Unit
        }
    }

    private fun progressInfo(snapshot: ProgressTracker.Snapshot): ForegroundInfo {
        val title = snapshot.title ?: applicationContext.getString(R.string.upload_notification_title)
        val notification = UploadNotification.build(applicationContext, snapshot.done, snapshot.total, title, snapshot.percent)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(UploadNotification.ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(UploadNotification.ID, notification)
        }
    }

    // MARK: Вспомогательные типы

    private class JobCancelledException : RuntimeException()
    private class JobPausedException : RuntimeException()

    private class UploadTerminalException(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

    /** Байты уже на сервере; упало только post-ready действие. */
    private class AttachFailedException(cause: Throwable) : RuntimeException(cause)

    private class PendingRetry(val jobId: String, val retryable: Boolean)

    /**
     * Агрегированный прогресс всех активных задач. `notify` дёргается не чаще раза в
     * [EMIT_INTERVAL_MILLIS] (события из OkHttp-потоков — только флаг, setForeground
     * делает потребитель канала).
     */
    private class ProgressTracker(private val notify: () -> Unit) {
        class Entry(val total: Long, val name: String, @Volatile var sent: Long = 0L)

        data class Snapshot(val done: Int, val total: Int, val title: String?, val percent: Int)

        private val lock = Any()
        private val entries = LinkedHashMap<String, Entry>()
        private var doneCount = 0
        private var doneBytes = 0L
        private var lastName: String? = null
        private var lastEmit = 0L

        fun register(id: String, total: Long, name: String) = synchronized(lock) {
            entries[id] = Entry(total, name)
            lastName = name
            emit(force = true)
        }

        fun progress(id: String, sent: Long) = synchronized(lock) {
            entries[id]?.sent = sent
            emit(force = false)
        }

        fun finish(id: String) = synchronized(lock) {
            entries.remove(id)?.let { doneBytes += it.sent }
            doneCount++
            emit(force = true)
        }

        fun remove(id: String) = synchronized(lock) {
            entries.remove(id)
            emit(force = true)
        }

        val doneTotal: Int get() = synchronized(lock) { doneCount }

        fun snapshot(): Snapshot = synchronized(lock) {
            val active = entries.values.toList()
            val totalBytes = doneBytes + active.sumOf { it.total }
            val sentBytes = doneBytes + active.sumOf { it.sent }
            val percent = if (totalBytes > 0) ((sentBytes * 100) / totalBytes).toInt() else 0
            Snapshot(done = doneCount, total = doneCount + active.size, title = lastName, percent = percent)
        }

        private fun emit(force: Boolean) {
            val now = SystemClock.elapsedRealtime()
            if (!force && now - lastEmit < EMIT_INTERVAL_MILLIS) return
            lastEmit = now
            notify()
        }
    }

    private val notifyChannel = Channel<Unit>(Channel.CONFLATED)

    private companion object {
        const val MAX_CONCURRENT_JOBS = 4
        const val MAX_RETRY_ATTEMPTS = 5
        const val MAX_PART_ATTEMPTS = 3
        const val PART_RETRY_BASE_MILLIS = 500L
        const val PROCESSING_POLL_MILLIS = 2_000L
        const val EMIT_INTERVAL_MILLIS = 250L
    }
}
