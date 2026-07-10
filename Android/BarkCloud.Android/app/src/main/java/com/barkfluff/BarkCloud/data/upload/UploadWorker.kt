package com.barkfluff.BarkCloud.data.upload

import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp
import io.grpc.Status
import io.grpc.StatusRuntimeException
import kotlinx.coroutines.runBlocking
import java.io.File
import java.io.FileNotFoundException

/** One foreground worker serializes all upload sources and resumes at AttachFile. */
class UploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val queue = UploadQueueStore(applicationContext)
        queue.initialize()
        val initial = queue.activeJobs()
        if (initial.isEmpty()) return Result.success()

        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.failure()

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, globalParam, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            val albums = AlbumRepository(grpc)
            val mediaDao = BarkCloudDatabase.get(applicationContext).mediaCloudStateDao()
            val total = initial.size
            var completed = 0
            setForeground(progressInfo(completed, total, null))

            while (true) {
                val job = queue.nextActive() ?: break
                try {
                    process(job, queue, transfer, cloud, albums, mediaDao, completed, total)
                    completed++
                    setForeground(progressInfo(completed, total, null))
                } catch (error: Throwable) {
                    if (shouldRetry(error) && runAttemptCount < MAX_RETRY_ATTEMPTS) {
                        queue.setPhase(job.id, resumePhase(job), error.message)
                        return Result.retry()
                    }
                    queue.setPhase(job.id, UploadPhase.FAILED, error.message ?: error::class.java.simpleName)
                    job.mediaKey?.let { key ->
                        mediaDao.byKey(key)?.let { state ->
                            mediaDao.upsert(state.copy(status = MediaCloudStatus.ERROR))
                        }
                    }
                }
            }

            applicationContext.getSystemService(NotificationManager::class.java)
                .notify(UploadNotification.ID, UploadNotification.finished(applicationContext, completed))
            Result.success()
        } finally {
            grpc.shutdown()
        }
    }

    private suspend fun process(
        initial: UploadJob,
        queue: UploadQueueStore,
        transfer: FileTransferService,
        cloud: CloudRepository,
        albums: AlbumRepository,
        mediaDao: com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao,
        completed: Int,
        total: Int,
    ) {
        var job = initial
        if (job.phase == UploadPhase.QUEUED) {
            val target = transfer.getUploadUrl(UploadFileType.CLOUD_FILE)
            queue.prepareUpload(job.id, target.fileId, target.url)
            job = requireNotNull(queue.byId(job.id))
        }

        if (job.phase == UploadPhase.UPLOADING) {
            job.mediaKey?.let { key ->
                mediaDao.byKey(key)?.let { state ->
                    mediaDao.upsert(state.copy(status = MediaCloudStatus.UPLOADING))
                }
            }
            val uploadUrl = requireNotNull(job.uploadUrl)
            var lastPersistedAt = 0L
            val onProgress: (Long, Long) -> Unit = { sent, bytesTotal ->
                val now = System.currentTimeMillis()
                if (now - lastPersistedAt >= PROGRESS_PERSIST_INTERVAL_MILLIS || sent == bytesTotal) {
                    lastPersistedAt = now
                    runBlocking {
                        queue.setProgress(job.id, sent, bytesTotal, UploadPhase.UPLOADING)
                        setForeground(progressInfo(completed, total, job.copy(bytesSent = sent, bytesTotal = bytesTotal)))
                    }
                }
            }
            val responseFileId = when {
                job.stagedFilePath != null -> {
                    val file = File(job.stagedFilePath)
                    if (!file.exists()) throw FileNotFoundException(job.stagedFilePath)
                    transfer.upload(file, job.fileName, uploadUrl, onProgress)
                }
                job.sourceUri != null -> transfer.upload(Uri.parse(job.sourceUri), job.fileName, uploadUrl, onProgress)
                else -> throw FileNotFoundException("No upload source for ${job.id}")
            }
            queue.markUploaded(job.id, responseFileId)
            job = requireNotNull(queue.byId(job.id))
        }

        if (job.phase == UploadPhase.UPLOADED) {
            val fileId = requireNotNull(job.preparedFileId)
            queue.setPhase(job.id, UploadPhase.ATTACHING)
            try {
                when (job.destination) {
                    UploadDestination.SYSTEM_BY_MEDIA_KIND ->
                        cloud.attachFile(fileId, "", job.fileName, routeByMediaKind = true)
                    UploadDestination.DIRECTORY ->
                        cloud.attachFile(fileId, requireNotNull(job.directoryId), job.fileName)
                }
            } catch (error: StatusRuntimeException) {
                if (error.errorCode() != FILE_ALREADY_ATTACHED) throw error
            }
            job.albumId?.let { albums.addItems(it, listOf(fileId)) }
            queue.complete(job)
            job.mediaKey?.let { key ->
                mediaDao.byKey(key)?.let { state ->
                    mediaDao.upsert(state.copy(status = MediaCloudStatus.IN_CLOUD, cloudFileId = fileId))
                }
            }
            if (job.source == UploadSource.BACKUP) {
                AutoUploadScheduler.runOnce(applicationContext, AutoUploadSettings(applicationContext).policy)
            }
        }
    }

    private fun resumePhase(job: UploadJob): UploadPhase =
        if (job.preparedFileId == null) UploadPhase.QUEUED else if (job.phase == UploadPhase.UPLOADING) UploadPhase.UPLOADING else UploadPhase.UPLOADED

    private fun shouldRetry(error: Throwable): Boolean = when (error) {
        is FileNotFoundException -> false
        is StatusRuntimeException -> error.status.code in setOf(
            Status.Code.UNAVAILABLE,
            Status.Code.DEADLINE_EXCEEDED,
            Status.Code.RESOURCE_EXHAUSTED,
        )
        else -> true
    }

    private fun progressInfo(done: Int, total: Int, current: UploadJob?): ForegroundInfo {
        val title = current?.fileName ?: applicationContext.getString(com.barkfluff.BarkCloud.R.string.upload_notification_title)
        val notification = UploadNotification.build(applicationContext, done, total, title)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(UploadNotification.ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(UploadNotification.ID, notification)
        }
    }

    private companion object {
        const val FILE_ALREADY_ATTACHED = "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B"
        const val PROGRESS_PERSIST_INTERVAL_MILLIS = 250L
        const val MAX_RETRY_ATTEMPTS = 5
    }
}
