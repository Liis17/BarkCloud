package com.barkfluff.BarkCloud.widgets

import android.content.Context

object StorageWidgetBridge {
    private const val PREFS_NAME = "barkcloud_storage_widget"
    private const val KEY_USED = "used"
    private const val KEY_LIMIT = "limit"
    private const val KEY_UPDATED_AT = "updated_at"

    fun update(context: Context, used: Long, limit: Long) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putLong(KEY_USED, used)
            .putLong(KEY_LIMIT, limit)
            .putLong(KEY_UPDATED_AT, System.currentTimeMillis())
            .apply()
        StorageWidgetProvider.refreshAll(context)
    }

    fun clear(context: Context) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().clear().apply()
        StorageWidgetProvider.refreshAll(context)
    }

    fun snapshot(context: Context): StorageSnapshot {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        return StorageSnapshot(
            used = prefs.getLong(KEY_USED, 0L),
            limit = prefs.getLong(KEY_LIMIT, 0L),
            updatedAt = prefs.getLong(KEY_UPDATED_AT, 0L),
        )
    }
}

data class StorageSnapshot(
    val used: Long,
    val limit: Long,
    val updatedAt: Long,
) {
    /** Был хотя бы один успешный снимок (`limit` = 0 у пользователя без квоты — это тоже данные). */
    val hasData: Boolean get() = updatedAt > 0
    val hasLimit: Boolean get() = limit > 0
    val percent: Int get() = if (limit > 0) ((used * 100) / limit).toInt().coerceIn(0, 100) else 0
}
