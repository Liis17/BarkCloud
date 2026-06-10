package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import java.util.concurrent.TimeUnit

object AutoUploadScheduler {
    private const val PERIODIC_NAME = "barkcloud_auto_upload_periodic"
    private const val ONCE_NAME = "barkcloud_auto_upload_once"

    fun enable(context: Context) {
        val constraints = Constraints.Builder()
            .setRequiredNetworkType(NetworkType.CONNECTED)
            .build()
        val periodic = PeriodicWorkRequestBuilder<AutoUploadWorker>(1, TimeUnit.HOURS)
            .setConstraints(constraints)
            .build()
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            PERIODIC_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            periodic,
        )
        runOnce(context)
    }

    fun runOnce(context: Context) {
        val constraints = Constraints.Builder()
            .setRequiredNetworkType(NetworkType.CONNECTED)
            .build()
        val request = OneTimeWorkRequestBuilder<AutoUploadWorker>()
            .setConstraints(constraints)
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(
            ONCE_NAME,
            ExistingWorkPolicy.REPLACE,
            request,
        )
    }

    fun disable(context: Context) {
        WorkManager.getInstance(context).cancelUniqueWork(ONCE_NAME)
        WorkManager.getInstance(context).cancelUniqueWork(PERIODIC_NAME)
    }
}
