package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import androidx.core.app.NotificationCompat
import com.barkfluff.BarkCloud.R

object UploadNotification {
    const val ID = 4201
    private const val CHANNEL_ID = "barkcloud_uploads"

    fun build(context: Context, done: Int, total: Int, title: String): Notification {
        ensureChannel(context)
        val max = total.coerceAtLeast(1)
        val progress = done.coerceIn(0, max)
        val builder = Notification.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(title)
            .setContentText(context.getString(R.string.upload_notification_progress, done, total))
            .setOngoing(done < total)
            .setOnlyAlertOnce(true)
            .setProgress(max, progress, false)

        if (Build.VERSION.SDK_INT >= 36) {
            val percent = ((progress * 100) / max).coerceIn(0, 100)
            builder.setStyle(
                Notification.ProgressStyle()
                    .setProgress(percent)
                    .setStyledByProgress(true)
            )
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
