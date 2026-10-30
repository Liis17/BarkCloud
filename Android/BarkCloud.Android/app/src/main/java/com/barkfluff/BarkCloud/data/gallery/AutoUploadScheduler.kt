package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OneTimeWorkRequest
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import kotlinx.coroutines.flow.first
import com.barkfluff.BarkCloud.data.upload.UniqueWorkEnqueuer
import java.util.concurrent.TimeUnit

object AutoUploadScheduler {
    private const val PERIODIC_NAME = "barkcloud_auto_upload_periodic"
    private const val ONCE_NAME = "barkcloud_auto_upload_once"

    fun apply(context: Context, policy: AutoUploadNetworkPolicy) {
        val settings = AutoUploadSettings(context)
        val policyChanged = policyChanged(settings.lastAppliedPolicy, policy)
        settings.lastAppliedPolicy = policy
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
        if (policyChanged) {
            UniqueWorkEnqueuer.cancelPending(ONCE_NAME)
            enqueueOnce(context, policy, ExistingWorkPolicy.REPLACE)
        }
    }

    fun runOnce(context: Context, policy: AutoUploadNetworkPolicy = AutoUploadSettings(context).policy) {
        if (policy == AutoUploadNetworkPolicy.OFF) return
        UniqueWorkEnqueuer.enqueue(WorkManager.getInstance(context), ONCE_NAME, request(policy))
    }

    fun disable(context: Context) {
        val settings = AutoUploadSettings(context)
        settings.policy = AutoUploadNetworkPolicy.OFF
        settings.lastAppliedPolicy = AutoUploadNetworkPolicy.OFF
        UniqueWorkEnqueuer.cancelPending(ONCE_NAME)
        WorkManager.getInstance(context).cancelUniqueWork(ONCE_NAME)
        WorkManager.getInstance(context).cancelUniqueWork(PERIODIC_NAME)
    }

    suspend fun disableAndAwait(context: Context) {
        val workManager = WorkManager.getInstance(context)
        disable(context)
        workManager.getWorkInfosForUniqueWorkFlow(ONCE_NAME).first { infos ->
            infos.all { it.state.isFinished }
        }
        workManager.getWorkInfosForUniqueWorkFlow(PERIODIC_NAME).first { infos ->
            infos.all { it.state.isFinished }
        }
    }

    private fun enqueueOnce(context: Context, policy: AutoUploadNetworkPolicy, existingPolicy: ExistingWorkPolicy) {
        val request = request(policy)
        WorkManager.getInstance(context).enqueueUniqueWork(ONCE_NAME, existingPolicy, request)
    }

    private fun request(policy: AutoUploadNetworkPolicy) = OneTimeWorkRequestBuilder<AutoUploadWorker>()
        .setConstraints(Constraints.Builder().setRequiredNetworkType(policy.networkType()).build())
        .build()

    internal fun policyChanged(previous: AutoUploadNetworkPolicy?, current: AutoUploadNetworkPolicy): Boolean =
        previous != current

    internal fun networkType(policy: AutoUploadNetworkPolicy): NetworkType = when (policy) {
        AutoUploadNetworkPolicy.WIFI_ONLY -> NetworkType.UNMETERED
        AutoUploadNetworkPolicy.ANY_NETWORK -> NetworkType.CONNECTED
        AutoUploadNetworkPolicy.OFF -> NetworkType.NOT_REQUIRED
    }

    private fun AutoUploadNetworkPolicy.networkType() = networkType(this)
}
