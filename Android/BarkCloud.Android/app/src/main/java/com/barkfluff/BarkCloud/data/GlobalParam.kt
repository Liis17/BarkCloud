package com.barkfluff.BarkCloud.data

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

@Suppress("DEPRECATION")
class GlobalParam(context: Context) {

    private val appContext = context.applicationContext

    private val prefs: SharedPreferences by lazy {
        val masterKey = MasterKey.Builder(appContext)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()

        EncryptedSharedPreferences.create(
            appContext,
            PREFS_NAME,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    var accessToken: String?
        get() = prefs.getString(KEY_ACCESS_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_ACCESS_TOKEN, value).apply()

    var accessTokenExpiresAt: Long
        get() = prefs.getLong(KEY_ACCESS_TOKEN_EXPIRES_AT, 0L)
        set(value) = prefs.edit().putLong(KEY_ACCESS_TOKEN_EXPIRES_AT, value).apply()

    var refreshToken: String?
        get() = prefs.getString(KEY_REFRESH_TOKEN, null)
        set(value) = prefs.edit().putString(KEY_REFRESH_TOKEN, value).apply()

    var refreshTokenExpiresAt: Long
        get() = prefs.getLong(KEY_REFRESH_TOKEN_EXPIRES_AT, 0L)
        set(value) = prefs.edit().putLong(KEY_REFRESH_TOKEN_EXPIRES_AT, value).apply()

    fun hasValidRefreshToken(): Boolean {
        val token = refreshToken ?: return false
        if (token.isBlank()) return false
        val expiresAt = refreshTokenExpiresAt
        return expiresAt == 0L || expiresAt > System.currentTimeMillis()
    }

    fun clearSession() {
        prefs.edit()
            .remove(KEY_ACCESS_TOKEN)
            .remove(KEY_ACCESS_TOKEN_EXPIRES_AT)
            .remove(KEY_REFRESH_TOKEN)
            .remove(KEY_REFRESH_TOKEN_EXPIRES_AT)
            .apply()
    }

    private companion object {
        const val PREFS_NAME = "barkcloud_secure_prefs"
        const val KEY_ACCESS_TOKEN = "access_token"
        const val KEY_ACCESS_TOKEN_EXPIRES_AT = "access_token_expires_at"
        const val KEY_REFRESH_TOKEN = "refresh_token"
        const val KEY_REFRESH_TOKEN_EXPIRES_AT = "refresh_token_expires_at"
    }
}
