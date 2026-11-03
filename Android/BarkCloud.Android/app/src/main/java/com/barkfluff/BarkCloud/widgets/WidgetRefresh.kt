package com.barkfluff.BarkCloud.widgets

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.barkfluff.BarkCloud.BarkCloudApplication
import kotlinx.coroutines.CancellationException
import java.util.concurrent.TimeUnit

/**
 * Обновляет данные всех виджетов из сети. Виджеты сами не ходят в gRPC: воркер работает
 * в процессе приложения с его сессией и пишет снимки в SharedPreferences / filesDir.
 */
class WidgetRefreshWorker(appContext: Context, params: WorkerParameters) : CoroutineWorker(appContext, params) {
    override suspend fun doWork(): Result {
        val app = applicationContext as BarkCloudApplication
        if (!app.globalParam.hasValidRefreshToken()) {
            WidgetRefresh.clearAll(applicationContext)
            return Result.success()
        }
        try {
            val storage = app.fileTransfer.storageInfo()
            StorageWidgetBridge.update(applicationContext, storage.used, storage.limit)
        } catch (e: Exception) {
            if (e is CancellationException) throw e
            // Сеть недоступна или ошибка сервера: остаётся последний снимок.
        }
        try {
            val summary = app.cloudRepository.trashSummary()
            TrashWidgetBridge.update(applicationContext, summary.count, summary.oldestPurgeAtMillis)
        } catch (e: Exception) {
            if (e is CancellationException) throw e
        }
        PhotoWidgetStore.refreshAll(app)
        return Result.success()
    }
}

object WidgetRefresh {
    private const val PERIODIC_NAME = "widgets-periodic-refresh"
    private const val NOW_NAME = "widgets-refresh-now"
    private val networkConstraints = Constraints.Builder()
        .setRequiredNetworkType(NetworkType.CONNECTED)
        .build()

    fun ensurePeriodic(context: Context) {
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(
            PERIODIC_NAME,
            ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<WidgetRefreshWorker>(15, TimeUnit.MINUTES)
                .setConstraints(networkConstraints)
                .build(),
        )
    }

    fun refreshNow(context: Context) {
        WorkManager.getInstance(context).enqueueUniqueWork(
            NOW_NAME,
            ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<WidgetRefreshWorker>()
                .setConstraints(networkConstraints)
                .build(),
        )
    }

    fun refreshNowIfAnyWidget(context: Context) {
        if (hasAnyWidgets(context)) refreshNow(context)
    }

    fun cancelPeriodicIfNoWidgets(context: Context) {
        if (!hasAnyWidgets(context)) WorkManager.getInstance(context).cancelUniqueWork(PERIODIC_NAME)
    }

    fun isSignedIn(context: Context): Boolean =
        (context.applicationContext as BarkCloudApplication).globalParam.hasValidRefreshToken()

    /** Выход из аккаунта: данные и фото чужого аккаунта не должны остаться на домашнем экране. */
    fun clearAll(context: Context) {
        StorageWidgetBridge.clear(context)
        TrashWidgetBridge.clear(context)
        PhotoWidgetStore.clearAll(context)
        PhotoWidgetProvider.refreshAll(context)
    }

    private fun hasAnyWidgets(context: Context): Boolean {
        val manager = AppWidgetManager.getInstance(context)
        return listOf(StorageWidgetProvider::class.java, TrashWidgetProvider::class.java, PhotoWidgetProvider::class.java)
            .any { manager.getAppWidgetIds(ComponentName(context, it)).isNotEmpty() }
    }
}
