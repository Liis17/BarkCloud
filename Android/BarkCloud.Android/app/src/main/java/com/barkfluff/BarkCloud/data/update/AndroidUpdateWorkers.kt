package com.barkfluff.BarkCloud.data.update

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.app.DownloadManager
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.BarkCloudApplication

class AndroidUpdateVerificationWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val attemptId = inputData.getString(KEY_ATTEMPT_ID) ?: return Result.failure()
        return when ((applicationContext as BarkCloudApplication).androidUpdateRepository.verifyAttempt(attemptId)) {
            UpdateVerificationResult.VERIFIED, UpdateVerificationResult.NOT_READY, UpdateVerificationResult.OBSOLETE -> Result.success()
        }
    }

    companion object {
        const val KEY_ATTEMPT_ID = "attempt_id"
    }
}

class AndroidUpdateCleanupWorker(
    context: Context,
    params: WorkerParameters,
) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        (applicationContext as BarkCloudApplication).androidUpdateRepository.cleanupAfterPackageReplacement()
        return Result.success()
    }
}

class UpdateDownloadCompleteReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != DownloadManager.ACTION_DOWNLOAD_COMPLETE) return
        val downloadId = intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1L)
        if (downloadId < 0) return
        (context.applicationContext as? BarkCloudApplication)
            ?.androidUpdateRepository
            ?.scheduleVerificationForDownload(downloadId)
    }
}

class UpdatePackageReplacedReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val request = OneTimeWorkRequestBuilder<AndroidUpdateCleanupWorker>().build()
        WorkManager.getInstance(context).enqueueUniqueWork(
            CLEANUP_WORK_NAME,
            ExistingWorkPolicy.KEEP,
            request,
        )
    }

    companion object {
        const val CLEANUP_WORK_NAME = "android-update-cleanup"
    }
}
