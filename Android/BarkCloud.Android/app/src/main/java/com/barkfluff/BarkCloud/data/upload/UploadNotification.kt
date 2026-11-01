package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import com.barkfluff.BarkCloud.MainActivity
import com.barkfluff.BarkCloud.R
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

object UploadNotification {
    /** Foreground-уведомление прохода; живёт до выхода воркера. */
    const val ID = 4201

    /** Итог успешного прохода. Отдельный ID: иначе WorkManager снимет его вместе с foreground-уведомлением. */
    const val FINISHED_ID = 4202

    private const val CHANNEL_ID = "barkcloud_uploads"
    private const val REQUEST_OPEN = 0
    private const val REQUEST_DISMISS = 1

    /** Android 16 (API 36): только там система показывает promoted ongoing и chip. */
    private const val PROMOTION_MIN_SDK = 36

    fun foreground(context: Context, tally: UploadRunTally): Notification {
        ensureChannel(context)
        val builder = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_barkcloud)
            .setContentTitle(context.getString(R.string.upload_notification_title))
            .setContentText(context.getString(R.string.upload_notification_progress, tally.done, tally.total))
            .setContentIntent(openAppIntent(context))
            .setDeleteIntent(dismissIntent(context))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(tally.total, tally.done, false)
        if (Build.VERSION.SDK_INT >= PROMOTION_MIN_SDK) {
            builder
                .setRequestPromotedOngoing(true)
                .setShortCriticalText(tally.chipText())
                .setStyle(NotificationCompat.ProgressStyle().setProgress(tally.percent).setStyledByProgress(true))
        }
        return builder.build()
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

    private fun dismissIntent(context: Context): PendingIntent = PendingIntent.getBroadcast(
        context,
        REQUEST_DISMISS,
        Intent(context, UploadNotificationDismissReceiver::class.java),
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

/**
 * Смахивание foreground-уведомления прохода. [markDismissed] вызывается из BroadcastReceiver:
 * это только volatile-запись, без suspend и без блокировок. До следующего прохода уведомление
 * не переписывается, иначе `setForeground` снова вернёт чип на экран.
 */
internal object UploadNotificationDismissal {
    val gate = UploadForegroundGate()

    fun reset() = gate.reset()

    fun markDismissed() = gate.markDismissed()
}

/**
 * Публикация foreground-уведомления с защитой от смахивания. Снимок счёта считается без блокировки,
 * затем под корутинным [Mutex] (не JVM-монитором) проверяется флаг и выполняется `post`.
 * Смахивание, пришедшее во время подсчёта или в очереди за блокировкой, отменяет публикацию.
 * Уже начатый `post` отменить нельзя: окно ограничено временем вызова `setForeground`.
 */
internal class UploadForegroundGate {
    private val mutex = Mutex()

    @Volatile private var dismissed = false

    fun reset() {
        dismissed = false
    }

    fun markDismissed() {
        dismissed = true
    }

    /** `null` из [snapshot] означает пустой счёт: публиковать нечего. */
    suspend fun publish(snapshot: suspend () -> UploadRunTally?, post: suspend (UploadRunTally) -> Unit) {
        if (dismissed) return
        val tally = snapshot() ?: return
        mutex.withLock {
            if (dismissed) return
            post(tally)
        }
    }
}
