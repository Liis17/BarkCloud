package com.barkfluff.BarkCloud.data.gallery

import android.content.Context

class AutoUploadSettings(context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_auto_upload", Context.MODE_PRIVATE)

    var enabled: Boolean
        get() = prefs.getBoolean(KEY_ENABLED, false)
        set(value) = prefs.edit().putBoolean(KEY_ENABLED, value).apply()

    var lastUploadedCount: Int
        get() = prefs.getInt(KEY_LAST_UPLOADED, 0)
        set(value) = prefs.edit().putInt(KEY_LAST_UPLOADED, value).apply()

    var lastRunAtMillis: Long
        get() = prefs.getLong(KEY_LAST_RUN, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_RUN, value).apply()

    companion object {
        private const val KEY_ENABLED = "enabled"
        private const val KEY_LAST_UPLOADED = "last_uploaded_count"
        private const val KEY_LAST_RUN = "last_run_at_millis"
    }
}
