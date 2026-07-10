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

    fun apply(context: Context, policy: AutoUploadNetworkPolicy) {
        if (policy == AutoUploadNetworkPolicy.OFF) {
            disable(context)
            return
        }
        val constraints = Constraints.Builder().setRequiredNetworkType(policy.networkType()).build()
        val periodic = PeriodicWorkRequestBuilder<AutoUploadWorker>(1, TimeUnit.HOURS)
            .setConstraints(constraints)
            .build()
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            PERIODIC_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            periodic,
        )
        runOnce(context, policy)
    }

    fun runOnce(context: Context, policy: AutoUploadNetworkPolicy = AutoUploadSettings(context).policy) {
        if (policy == AutoUploadNetworkPolicy.OFF) return
        val request = OneTimeWorkRequestBuilder<AutoUploadWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(policy.networkType()).build())
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(ONCE_NAME, ExistingWorkPolicy.KEEP, request)
    }

    fun disable(context: Context) {
        WorkManager.getInstance(context).cancelUniqueWork(ONCE_NAME)
        WorkManager.getInstance(context).cancelUniqueWork(PERIODIC_NAME)
    }

    private fun AutoUploadNetworkPolicy.networkType(): NetworkType = when (this) {
        AutoUploadNetworkPolicy.WIFI_ONLY -> NetworkType.UNMETERED
        AutoUploadNetworkPolicy.ANY_NETWORK -> NetworkType.CONNECTED
        AutoUploadNetworkPolicy.OFF -> NetworkType.NOT_REQUIRED
    }
}
