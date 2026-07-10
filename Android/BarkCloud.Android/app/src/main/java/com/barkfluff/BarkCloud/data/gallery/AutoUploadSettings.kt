package com.barkfluff.BarkCloud.data.gallery

import android.content.Context

class AutoUploadSettings(context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_auto_upload", Context.MODE_PRIVATE)

    var policy: AutoUploadNetworkPolicy
        get() {
            val saved = prefs.getString(KEY_POLICY, null)
            if (saved != null) return runCatching { AutoUploadNetworkPolicy.valueOf(saved) }
                .getOrDefault(AutoUploadNetworkPolicy.WIFI_ONLY)
            return if (prefs.contains(KEY_LEGACY_ENABLED)) {
                if (prefs.getBoolean(KEY_LEGACY_ENABLED, false)) AutoUploadNetworkPolicy.ANY_NETWORK
                else AutoUploadNetworkPolicy.OFF
            } else AutoUploadNetworkPolicy.WIFI_ONLY
        }
        set(value) = prefs.edit().putString(KEY_POLICY, value.name).apply()

    val enabled: Boolean get() = policy != AutoUploadNetworkPolicy.OFF

    var lastUploadedCount: Int
        get() = prefs.getInt(KEY_LAST_UPLOADED, 0)
        set(value) = prefs.edit().putInt(KEY_LAST_UPLOADED, value).apply()

    var lastRunAtMillis: Long
        get() = prefs.getLong(KEY_LAST_RUN, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_RUN, value).apply()

    companion object {
        private const val KEY_LEGACY_ENABLED = "enabled"
        private const val KEY_POLICY = "network_policy"
        private const val KEY_LAST_UPLOADED = "last_uploaded_count"
        private const val KEY_LAST_RUN = "last_run_at_millis"
    }
}

enum class AutoUploadNetworkPolicy { WIFI_ONLY, ANY_NETWORK, OFF }
