package com.barkfluff.BarkCloud.data.cache

import android.content.Context

class FileCacheSettings(context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_cache", Context.MODE_PRIVATE)

    var maxCacheBytes: Long
        get() = prefs.getLong(KEY_MAX_BYTES, DEFAULT_MAX_BYTES)
        set(value) = prefs.edit().putLong(KEY_MAX_BYTES, value).apply()

    var staleMaxAgeMillis: Long
        get() = prefs.getLong(KEY_STALE_AGE, DEFAULT_STALE_AGE_MILLIS)
        set(value) = prefs.edit().putLong(KEY_STALE_AGE, value).apply()

    var lastSweepAtMillis: Long
        get() = prefs.getLong(KEY_LAST_SWEEP, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_SWEEP, value).apply()

    companion object {
        const val NEVER = 0L
        const val DAY_MILLIS = 24L * 60L * 60L * 1000L
        const val DEFAULT_MAX_BYTES = 5L * 1024L * 1024L * 1024L
        const val DEFAULT_STALE_AGE_MILLIS = 7L * DAY_MILLIS

        private const val KEY_MAX_BYTES = "max_cache_bytes"
        private const val KEY_STALE_AGE = "stale_max_age_millis"
        private const val KEY_LAST_SWEEP = "last_sweep_at_millis"
    }
}
