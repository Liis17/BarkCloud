package com.barkfluff.BarkCloud.data.gallery

import android.content.Context

class AutoUploadSettings(context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_auto_upload", Context.MODE_PRIVATE)

    var policy: AutoUploadNetworkPolicy
        get() {
            val saved = prefs.getString(KEY_POLICY, null)
            if (saved != null) return runCatching { AutoUploadNetworkPolicy.valueOf(saved) }
                .getOrDefault(AutoUploadNetworkPolicy.OFF)
            return if (prefs.contains(KEY_LEGACY_ENABLED)) {
                if (prefs.getBoolean(KEY_LEGACY_ENABLED, false)) AutoUploadNetworkPolicy.ANY_NETWORK
                else AutoUploadNetworkPolicy.OFF
            } else AutoUploadNetworkPolicy.OFF
        }
        set(value) = prefs.edit().putString(KEY_POLICY, value.name).apply()

    val enabled: Boolean get() = policy != AutoUploadNetworkPolicy.OFF

    // null preserves the existing all-folders backup until the user chooses folders.
    var folderKeys: Set<String>?
        get() = prefs.getStringSet("folder_keys", null)?.toSet()
        set(value) = prefs.edit().putStringSet("folder_keys", value?.toSet()).apply()

    fun includesFolder(key: String): Boolean = folderKeys?.contains(key) ?: true

    fun setFolderEnabled(key: String, enabled: Boolean, knownFolders: Set<String>) {
        val selected = folderKeys ?: if (this.enabled) knownFolders else emptySet()
        folderKeys = if (enabled) selected + key else selected - key
        if (enabled && !this.enabled) policy = AutoUploadNetworkPolicy.WIFI_ONLY
    }

    var lastUploadedCount: Int
        get() = prefs.getInt(KEY_LAST_UPLOADED, 0)
        set(value) = prefs.edit().putInt(KEY_LAST_UPLOADED, value).apply()

    var lastRunAtMillis: Long
        get() = prefs.getLong(KEY_LAST_RUN, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_RUN, value).apply()

    var lastAppliedPolicy: AutoUploadNetworkPolicy?
        get() = prefs.getString(KEY_LAST_APPLIED_POLICY, null)?.let {
            runCatching { AutoUploadNetworkPolicy.valueOf(it) }.getOrNull()
        }
        set(value) {
            val edit = prefs.edit()
            if (value == null) edit.remove(KEY_LAST_APPLIED_POLICY)
            else edit.putString(KEY_LAST_APPLIED_POLICY, value.name)
            edit.commit()
        }

    companion object {
        private const val KEY_LEGACY_ENABLED = "enabled"
        private const val KEY_POLICY = "network_policy"
        private const val KEY_LAST_UPLOADED = "last_uploaded_count"
        private const val KEY_LAST_RUN = "last_run_at_millis"
        private const val KEY_LAST_APPLIED_POLICY = "last_applied_network_policy"
    }
}

enum class AutoUploadNetworkPolicy { WIFI_ONLY, ANY_NETWORK, OFF }
