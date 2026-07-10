package com.barkfluff.BarkCloud.data.gallery

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import com.barkfluff.BarkCloud.data.upload.UploadSource
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp

/** Finds changed MediaStore entries and feeds the common upload queue in bounded batches. */
class AutoUploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val settings = AutoUploadSettings(applicationContext)
        if (!settings.enabled) return Result.success()
        if (!hasMediaPermission()) return Result.success()
        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.success()

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, globalParam, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            discoverMissing(cloud, settings)
            Result.success()
        } catch (_: Exception) {
            Result.retry()
        } finally {
            grpc.shutdown()
        }
    }

    private suspend fun discoverMissing(cloud: CloudRepository, settings: AutoUploadSettings) {
        val database = BarkCloudDatabase.get(applicationContext)
        val mediaDao = database.mediaCloudStateDao()
        val queue = UploadQueueStore(applicationContext, database.uploadDao())
        queue.initialize()
        val existing = mediaDao.all().associateBy { it.mediaKey }
        val capacity = (MAX_ACTIVE_BACKUP_JOBS - queue.activeBackup().size).coerceAtLeast(0)
        if (capacity == 0) return

        val candidates = DeviceMediaStore.query(applicationContext)
            .filter { media ->
                val state = existing[media.mediaKey]
                state == null ||
                    state.dateModifiedSeconds != media.dateModifiedSeconds ||
                    state.sizeBytes != media.sizeBytes ||
                    state.status in setOf(MediaCloudStatus.NOT_IN_CLOUD, MediaCloudStatus.ERROR)
            }
            .take(minOf(MAX_ITEMS_PER_RUN, capacity))

        var queued = 0
        for (chunk in candidates.chunked(HASH_BATCH_SIZE)) {
            val hashed = chunk.mapNotNull { media ->
                MediaHasher.sha256(applicationContext, media.uri)?.let { hash -> media to hash }
            }
            hashed.forEach { (media, hash) ->
                mediaDao.upsert(
                    MediaCloudState(
                        mediaKey = media.mediaKey,
                        mediaId = media.id,
                        isVideo = media.isVideo,
                        dateModifiedSeconds = media.dateModifiedSeconds,
                        sizeBytes = media.sizeBytes,
                        hash = hash,
                        status = MediaCloudStatus.CHECKING,
                        cloudFileId = null,
                    ),
                )
            }
            val presence = cloud.checkFileHashes(hashed.map { it.second })
            for ((media, hash) in hashed) {
                if (presence[hash] == true) {
                    mediaDao.upsert(
                        MediaCloudState(
                            mediaKey = media.mediaKey,
                            mediaId = media.id,
                            isVideo = media.isVideo,
                            dateModifiedSeconds = media.dateModifiedSeconds,
                            sizeBytes = media.sizeBytes,
                            hash = hash,
                            status = MediaCloudStatus.IN_CLOUD,
                            cloudFileId = null,
                        ),
                    )
                    continue
                }
                queue.enqueue(
                    uri = media.uri,
                    fileName = media.name,
                    source = UploadSource.BACKUP,
                    destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                    mediaKey = media.mediaKey,
                    mediaHash = hash,
                    stageSource = false,
                )
                mediaDao.upsert(
                    MediaCloudState(
                        mediaKey = media.mediaKey,
                        mediaId = media.id,
                        isVideo = media.isVideo,
                        dateModifiedSeconds = media.dateModifiedSeconds,
                        sizeBytes = media.sizeBytes,
                        hash = hash,
                        status = MediaCloudStatus.QUEUED,
                        cloudFileId = null,
                    ),
                )
                queued++
            }
        }
        settings.lastUploadedCount = queued
        settings.lastRunAtMillis = System.currentTimeMillis()
        if (queued > 0) UploadScheduler.enqueue(applicationContext, userInitiated = false)
    }

    private companion object {
        const val MAX_ITEMS_PER_RUN = 100
        const val MAX_ACTIVE_BACKUP_JOBS = 20
        const val HASH_BATCH_SIZE = 50
    }

    private fun hasMediaPermission(): Boolean = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED ||
            ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.READ_MEDIA_VIDEO) == PackageManager.PERMISSION_GRANTED
    } else {
        ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
    }
}
