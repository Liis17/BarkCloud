package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import com.barkfluff.BarkCloud.data.NotificationPermissionRequests
import androidx.work.Constraints
import androidx.work.BackoffPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import kotlinx.coroutines.flow.first
import java.util.concurrent.TimeUnit

object UploadScheduler {
    private const val WORK_NAME = "barkcloud_upload_queue"

    fun enqueue(context: Context, userInitiated: Boolean = true) {
        if (userInitiated) NotificationPermissionRequests.request()
        val request = OneTimeWorkRequestBuilder<UploadWorker>()
            .setConstraints(
                Constraints.Builder()
                    .setRequiredNetworkType(NetworkType.CONNECTED)
                    .build()
            )
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .apply {
                if (userInitiated) {
                    setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                }
            }
            .build()
        UniqueWorkEnqueuer.enqueue(WorkManager.getInstance(context), WORK_NAME, request)
    }

    suspend fun cancelAndAwait(context: Context) {
        val workManager = WorkManager.getInstance(context)
        workManager.cancelUniqueWork(WORK_NAME)
        workManager.getWorkInfosForUniqueWorkFlow(WORK_NAME).first { infos ->
            infos.all { it.state.isFinished }
        }
    }

}
