package com.barkfluff.BarkCloud.data.upload

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Получает удаление уведомления из `deleteIntent`; доступен только приложению (`exported=false`). */
class UploadNotificationDismissReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        UploadNotificationDismissal.markDismissed()
    }
}
