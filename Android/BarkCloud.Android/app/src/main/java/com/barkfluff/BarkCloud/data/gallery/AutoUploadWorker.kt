package com.barkfluff.BarkCloud.data.gallery

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import kotlinx.coroutines.CancellationException

/** Finds selected MediaStore entries and persists lazy jobs in the common upload queue. */
class AutoUploadWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val settings = AutoUploadSettings(applicationContext)
        if (!settings.enabled) return Result.success()
        val globalParam = GlobalParam(applicationContext)
        if (!globalParam.hasValidRefreshToken()) return Result.success()

        val queue = UploadQueueStore(applicationContext)
        return try {
            queue.initialize()
            val includeImages = hasMediaPermission(isVideo = false)
            val includeVideos = hasMediaPermission(isVideo = true)
            val discovery = BackupQueueDiscovery(
                queue = queue,
                isEnabled = { settings.enabled },
                includesFolder = settings::includesFolder,
            )
            val queued = discoverAndWakeUploadQueue(
                discover = {
                    discovery.discover { onBatch ->
                        if (includeImages || includeVideos) {
                            DeviceMediaStore.forEachBatch(
                                context = applicationContext,
                                includeImages = includeImages,
                                includeVideos = includeVideos,
                                onBatch = onBatch,
                            )
                        }
                    }
                },
                activeCount = queue::activeCount,
                enqueueUpload = {
                    UploadScheduler.enqueue(applicationContext, userInitiated = false)
                }
            )
            settings.lastUploadedCount = queued
            settings.lastRunAtMillis = System.currentTimeMillis()
            Result.success()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: SecurityException) {
            if (queue.activeCount() > 0) UploadScheduler.enqueue(applicationContext, userInitiated = false)
            Result.success()
        } catch (_: Exception) {
            Result.retry()
        }
    }

    private fun hasMediaPermission(isVideo: Boolean): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            return ContextCompat.checkSelfPermission(
                applicationContext,
                Manifest.permission.READ_EXTERNAL_STORAGE,
            ) == PackageManager.PERMISSION_GRANTED
        }
        val mediaPermission = if (isVideo) Manifest.permission.READ_MEDIA_VIDEO else Manifest.permission.READ_MEDIA_IMAGES
        if (ContextCompat.checkSelfPermission(applicationContext, mediaPermission) == PackageManager.PERMISSION_GRANTED) return true
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE &&
            ContextCompat.checkSelfPermission(
                applicationContext,
                Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED,
            ) == PackageManager.PERMISSION_GRANTED
    }
}
