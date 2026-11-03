package com.barkfluff.BarkCloud.widgets

import android.content.Context

object TrashWidgetBridge {
    private const val PREFS_NAME = "barkcloud_trash_widget"
    private const val KEY_HAS_DATA = "has_data"
    private const val KEY_COUNT = "count"
    private const val KEY_OLDEST_PURGE_AT = "oldest_purge_at"

    /** Сохраняет абсолютный срок удаления: обратный отсчёт пересчитывается при каждом рендере. */
    fun update(context: Context, count: Int, oldestPurgeAtMillis: Long?) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_HAS_DATA, true)
            .putInt(KEY_COUNT, count)
            .putLong(KEY_OLDEST_PURGE_AT, oldestPurgeAtMillis ?: 0L)
            .apply()
        TrashWidgetProvider.refreshAll(context)
    }

    fun clear(context: Context) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().clear().apply()
        TrashWidgetProvider.refreshAll(context)
    }

    fun snapshot(context: Context): TrashSnapshot? {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        if (!prefs.getBoolean(KEY_HAS_DATA, false)) return null
        val purgeAt = prefs.getLong(KEY_OLDEST_PURGE_AT, 0L)
        return TrashSnapshot(
            count = prefs.getInt(KEY_COUNT, 0),
            oldestPurgeAtMillis = purgeAt.takeIf { it > 0L },
        )
    }
}

data class TrashSnapshot(
    val count: Int,
    val oldestPurgeAtMillis: Long?,
)
