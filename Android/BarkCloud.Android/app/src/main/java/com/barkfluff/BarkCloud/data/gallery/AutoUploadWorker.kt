package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp
import com.barkfluff.BarkCloud.data.upload.UploadNotification

class AutoUploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val settings = AutoUploadSettings(applicationContext)
        if (!settings.enabled) return Result.success()

        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.success()

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, globalParam, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            val uploaded = uploadMissing(cloud)
            settings.lastUploadedCount = uploaded
            settings.lastRunAtMillis = System.currentTimeMillis()
            Result.success()
        } catch (_: Exception) {
            Result.retry()
        } finally {
            grpc.shutdown()
        }
    }

    private suspend fun uploadMissing(cloud: CloudRepository): Int {
        val media = DeviceMediaStore.query(applicationContext).take(MAX_ITEMS_PER_RUN)
        setForeground(progressInfo(0, media.size.coerceAtLeast(1)))
        var uploaded = 0
        var scanned = 0
        for (chunk in media.chunked(HASH_BATCH_SIZE)) {
            val hashed = chunk.mapNotNull { item ->
                MediaHasher.sha256(applicationContext, item.uri)?.let { hash -> item to hash }
            }
            val presence = cloud.checkFileHashes(hashed.map { it.second })
            for ((item, hash) in hashed) {
                if (presence[hash] != true) {
                    cloud.uploadFile(item.uri, item.name)
                    uploaded++
                }
                scanned++
                setForeground(progressInfo(scanned, media.size.coerceAtLeast(1)))
            }
        }
        return uploaded
    }

    private fun progressInfo(done: Int, total: Int): ForegroundInfo {
        val notification = UploadNotification.build(
            applicationContext,
            done,
            total,
            applicationContext.getString(com.barkfluff.BarkCloud.R.string.upload_notification_title),
        )
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(UploadNotification.ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(UploadNotification.ID, notification)
        }
    }

    private companion object {
        const val MAX_ITEMS_PER_RUN = 200
        const val HASH_BATCH_SIZE = 50
    }
}
