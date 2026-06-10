package com.barkfluff.BarkCloud.data.upload

import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.grpc.ClientMetadataInterceptor
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.net.InsecureHttp
import java.io.File

class UploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val queue = UploadQueueStore(applicationContext)
        val initial = queue.pending()
        if (initial.isEmpty()) return Result.success()

        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.failure()

        val grpc = GrpcManager(globalParam, ClientMetadataInterceptor.create(applicationContext))
        return try {
            val transfer = FileTransferService(applicationContext, grpc, globalParam, InsecureHttp.client)
            val cloud = CloudRepository(grpc, transfer)
            val albums = AlbumRepository(grpc)
            val total = initial.size
            setForeground(progressInfo(0, total))
            var done = 0
            for (item in initial) {
                val file = File(item.filePath)
                if (file.exists()) {
                    val fileId = cloud.uploadFile(file, item.fileName, item.directoryId)
                    if (item.albumId != null) {
                        albums.addItems(item.albumId, listOf(fileId))
                    }
                }
                queue.remove(item.id)
                done++
                setForeground(progressInfo(done, total))
            }
            applicationContext.getSystemService(NotificationManager::class.java)
                .notify(UploadNotification.ID, UploadNotification.finished(applicationContext, total))
            Result.success()
        } catch (_: Exception) {
            Result.retry()
        } finally {
            grpc.shutdown()
        }
    }

    private fun progressInfo(done: Int, total: Int): ForegroundInfo =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(
                UploadNotification.ID,
                UploadNotification.build(
                    applicationContext,
                    done,
                    total,
                    applicationContext.getString(com.barkfluff.BarkCloud.R.string.upload_notification_title),
                ),
                ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
            )
        } else {
            ForegroundInfo(
                UploadNotification.ID,
                UploadNotification.build(
                    applicationContext,
                    done,
                    total,
                    applicationContext.getString(com.barkfluff.BarkCloud.R.string.upload_notification_title),
                ),
            )
        }
}
