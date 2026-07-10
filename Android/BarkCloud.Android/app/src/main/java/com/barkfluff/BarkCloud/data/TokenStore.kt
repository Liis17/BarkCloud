package com.barkfluff.BarkCloud.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Keystore-backed хранилище токенов в credential-protected storage приложения. */
class TokenStore(context: Context) {
    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    init {
        migrateLegacyTokens()
    }

    fun read(): TokenBundle? = runCatching {
        val iv = prefs.getString(KEY_IV, null) ?: return null
        val payload = prefs.getString(KEY_PAYLOAD, null) ?: return null
        val plain = cipher(Cipher.DECRYPT_MODE, Base64.decode(iv, Base64.NO_WRAP))
            .doFinal(Base64.decode(payload, Base64.NO_WRAP))
        JSONObject(plain.decodeToString()).let {
            TokenBundle(
                accessToken = it.optString("accessToken"),
                accessTokenExpiresAt = it.optLong("accessTokenExpiresAt"),
                refreshToken = it.optString("refreshToken"),
                refreshTokenExpiresAt = it.optLong("refreshTokenExpiresAt"),
            )
        }
    }.getOrElse {
        clear()
        null
    }

    fun save(bundle: TokenBundle) {
        val encryption = cipher(Cipher.ENCRYPT_MODE)
        val plain = JSONObject()
            .put("accessToken", bundle.accessToken)
            .put("accessTokenExpiresAt", bundle.accessTokenExpiresAt)
            .put("refreshToken", bundle.refreshToken)
            .put("refreshTokenExpiresAt", bundle.refreshTokenExpiresAt)
            .toString()
            .encodeToByteArray()
        val encrypted = encryption.doFinal(plain)
        prefs.edit()
            .putString(KEY_IV, Base64.encodeToString(encryption.iv, Base64.NO_WRAP))
            .putString(KEY_PAYLOAD, Base64.encodeToString(encrypted, Base64.NO_WRAP))
            .apply()
    }

    fun clear() {
        prefs.edit().clear().apply()
    }

    private fun cipher(mode: Int, iv: ByteArray? = null): Cipher =
        Cipher.getInstance(TRANSFORMATION).apply {
            if (mode == Cipher.ENCRYPT_MODE) {
                init(mode, secretKey())
            } else {
                init(mode, secretKey(), GCMParameterSpec(GCM_TAG_BITS, requireNotNull(iv)))
            }
        }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEY_STORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    @Suppress("DEPRECATION")
    private fun migrateLegacyTokens() {
        if (prefs.contains(KEY_PAYLOAD)) return
        val legacy = runCatching {
            val key = MasterKey.Builder(appContext)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            EncryptedSharedPreferences.create(
                appContext,
                LEGACY_PREFS_NAME,
                key,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
            )
        }.getOrNull() ?: return

        val refresh = legacy.getString(LEGACY_REFRESH_TOKEN, null).orEmpty()
        if (refresh.isNotBlank()) {
            save(
                TokenBundle(
                    accessToken = legacy.getString(LEGACY_ACCESS_TOKEN, null).orEmpty(),
                    accessTokenExpiresAt = legacy.getLong(LEGACY_ACCESS_EXPIRES_AT, 0L),
                    refreshToken = refresh,
                    refreshTokenExpiresAt = legacy.getLong(LEGACY_REFRESH_EXPIRES_AT, 0L),
                ),
            )
        }
        legacy.edit().clear().apply()
    }

    companion object {
        private const val PREFS_NAME = "barkcloud_token_store"
        private const val KEY_IV = "iv"
        private const val KEY_PAYLOAD = "payload"
        private const val KEY_ALIAS = "BarkCloud.TokenStore.v1"
        private const val ANDROID_KEY_STORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val GCM_TAG_BITS = 128

        private const val LEGACY_PREFS_NAME = "barkcloud_secure_prefs"
        private const val LEGACY_ACCESS_TOKEN = "access_token"
        private const val LEGACY_ACCESS_EXPIRES_AT = "access_token_expires_at"
        private const val LEGACY_REFRESH_TOKEN = "refresh_token"
        private const val LEGACY_REFRESH_EXPIRES_AT = "refresh_token_expires_at"
    }
}

data class TokenBundle(
    val accessToken: String,
    val accessTokenExpiresAt: Long,
    val refreshToken: String,
    val refreshTokenExpiresAt: Long,
)
