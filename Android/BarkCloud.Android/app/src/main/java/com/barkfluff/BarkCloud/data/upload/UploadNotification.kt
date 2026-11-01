package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.core.app.NotificationCompat
import androidx.core.net.toUri
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R
import kotlinx.coroutines.withContext
import java.util.UUID
import kotlin.coroutines.CoroutineContext

object UploadNotification {
    /** Foreground-уведомление прохода; живёт до выхода воркера. */
    const val ID = 4201

    /** Итог успешного прохода. Отдельный ID: иначе WorkManager снимет его вместе с foreground-уведомлением. */
    const val FINISHED_ID = 4202

    /** Токен прохода в extras `deleteIntent`: смахивание учитывается только для своего прохода. */
    const val EXTRA_RUN_TOKEN = "com.barkfluff.BarkCloud.upload.RUN_TOKEN"

    private const val CHANNEL_ID = "barkcloud_uploads"
    private const val DISMISS_URI_PREFIX = "barkcloud-upload://dismiss/"
    private const val REQUEST_OPEN = 0
    private const val REQUEST_DISMISS = 1

    /** Android 16 (API 36): только там система показывает promoted ongoing и chip. */
    private const val PROMOTION_MIN_SDK = 36

    fun foreground(context: Context, tally: UploadRunTally, runToken: String): Notification {
        ensureChannel(context)
        val builder = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_barkcloud)
            .setContentTitle(context.getString(R.string.upload_notification_title))
            .setContentText(context.getString(R.string.upload_notification_progress, tally.done, tally.total))
            .setContentIntent(openAppIntent(context))
            .setDeleteIntent(dismissIntent(context, runToken))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(tally.total, tally.done, false)
            .addExtras(Bundle().apply { putString(EXTRA_RUN_TOKEN, runToken) })
        if (Build.VERSION.SDK_INT >= PROMOTION_MIN_SDK) {
            builder
                .setRequestPromotedOngoing(true)
                .setShortCriticalText(tally.chipText())
                .setStyle(NotificationCompat.ProgressStyle().setProgress(tally.percent).setStyledByProgress(true))
        }
        return builder.build()
    }

    /**
     * Обновление уже зарегистрированного foreground-уведомления тем же ID. Идёт напрямую через
     * NotificationManager, а не через `setForeground`: очередь WorkManager публикует позже вызова,
     * и смахнутое уведомление могло бы вернуться. Вызывать только через [UploadRefreshPublisher].
     */
    fun update(context: Context, tally: UploadRunTally, runToken: String) {
        context.getSystemService(NotificationManager::class.java).notify(ID, foreground(context, tally, runToken))
    }

    /**
     * Есть ли видимая foreground-регистрация этого прохода: уведомление с нашим ID, токеном прохода в
     * extras и флагом `FLAG_FOREGROUND_SERVICE`. Без неё прямое обновление создало бы обычное,
     * смахиваемое уведомление до регистрации FGS.
     */
    fun isForegroundRegistered(context: Context, runToken: String): Boolean {
        val active = context.getSystemService(NotificationManager::class.java).activeNotifications.map { sbn ->
            ActiveNotificationInfo(
                id = sbn.id,
                flags = sbn.notification.flags,
                runToken = sbn.notification.extras?.getString(EXTRA_RUN_TOKEN),
            )
        }
        return isRegisteredForeground(active, runToken)
    }

    fun finished(context: Context, total: Int): Notification {
        ensureChannel(context)
        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(context.getString(R.string.upload_notification_done))
            .setContentText(context.resources.getQuantityString(R.plurals.upload_notification_done_count, total, total))
            .setOnlyAlertOnce(true)
            .build()
    }

    private fun openAppIntent(context: Context): PendingIntent = PendingIntent.getActivity(
        context,
        REQUEST_OPEN,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    /**
     * Data URI с токеном делает PendingIntent уникальным для прохода: `FLAG_UPDATE_CURRENT` обновляет
     * только PendingIntent того же прохода, а уведомления прошлых проходов сохраняют свой токен.
     */
    private fun dismissIntent(context: Context, runToken: String): PendingIntent = PendingIntent.getBroadcast(
        context,
        REQUEST_DISMISS,
        Intent(context, UploadNotificationDismissReceiver::class.java)
            .setData("$DISMISS_URI_PREFIX$runToken".toUri())
            .putExtra(EXTRA_RUN_TOKEN, runToken),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            context.getString(R.string.upload_notification_channel),
            NotificationManager.IMPORTANCE_LOW,
        )
        manager.createNotificationChannel(channel)
    }
}

/** Уведомление из системного списка активных: нужные поля для проверки регистрации. */
internal data class ActiveNotificationInfo(val id: Int, val flags: Int, val runToken: String?)

/**
 * Чистая проверка регистрации: уведомление нашего ID, с токеном [runToken] и флагом FGS.
 * Отсутствие, чужой токен или отсутствие флага FGS дают `false`.
 */
internal fun isRegisteredForeground(active: List<ActiveNotificationInfo>, runToken: String): Boolean =
    active.any { info ->
        info.id == UploadNotification.ID &&
            info.runToken == runToken &&
            (info.flags and Notification.FLAG_FOREGROUND_SERVICE) != 0
    }

/**
 * Смахивание foreground-уведомления прохода. Токен прохода задаёт, чьё смахивание учитывается:
 * уведомление прошлого прохода на текущий не влияет. Новый проход сбрасывает прошлое смахивание.
 */
internal object UploadNotificationDismissal {
    @Volatile private var activeRun: String? = null
    @Volatile private var dismissedRun: String? = null

    /** Тестовый перехватчик: вызывается с токеном каждой доставки, даже неактивного прохода. */
    @Volatile var onDismissReceived: ((String?) -> Unit)? = null

    fun beginRun(): String {
        val token = UUID.randomUUID().toString()
        dismissedRun = null
        activeRun = token
        return token
    }

    /** Вызывается из BroadcastReceiver на главном потоке: только volatile-записи, без блокировок и suspend. */
    fun markDismissed(runToken: String?) {
        if (runToken != null && runToken == activeRun) dismissedRun = runToken
        onDismissReceived?.invoke(runToken)
    }

    fun isDismissed(runToken: String): Boolean = dismissedRun == runToken
}

/**
 * Обновление счёта прохода без WorkManager. Регистрация FGS проверяется до снимка: до её появления
 * прямое обновление не создаёт уведомление. Подтверждённая регистрация запоминается на проход и
 * больше не проверяется, поэтому не зависит от флага, который система может сбросить.
 * [snapshot] (запрос к Room) выполняется вне главного потока. Проверка смахивания и [notify] идут
 * синхронно на [mainContext] без приостановки между ними. BroadcastReceiver работает на главном
 * потоке, поэтому смахивание либо учтено до проверки, либо приходит после возврата из [notify].
 */
internal class UploadRefreshPublisher(
    private val mainContext: CoroutineContext,
    private val isDismissed: () -> Boolean,
    private val isRegistered: () -> Boolean,
    private val notify: (UploadRunTally) -> Unit,
) {
    @Volatile private var registrationSeen = false

    /** `null` из [snapshot] означает пустой счёт: публиковать нечего. */
    suspend fun publish(snapshot: suspend () -> UploadRunTally?) {
        if (isDismissed() || !hasRegistration()) return
        val tally = snapshot() ?: return
        withContext(mainContext) {
            if (isDismissed()) return@withContext
            notify(tally)
        }
    }

    private fun hasRegistration(): Boolean {
        if (registrationSeen) return true
        if (!isRegistered()) return false
        registrationSeen = true
        return true
    }
}
