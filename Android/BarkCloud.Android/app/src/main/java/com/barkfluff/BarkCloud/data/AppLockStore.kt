package com.barkfluff.BarkCloud.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec

/**
 * Хранилище PIN-кода App Lock. PIN дерижируется PBKDF2 (как на iOS, вне
 * Keystore — деривация не про хранение секрета, а про защиту от брутфорса),
 * результат (соль+хэш) шифруется AES-256-GCM ключом в Android Keystore —
 * так же, как [TokenStore] шифрует токены. Двойная защита: PBKDF2 против
 * офлайн-перебора, Keystore-шифрование против чтения prefs на рутованном устройстве.
 */
class AppLockStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    val isEnabled: Boolean get() = prefs.getBoolean(KEY_ENABLED, false)

    var failedAttempts: Int
        get() = prefs.getInt(KEY_FAILED_ATTEMPTS, 0)
        private set(value) = prefs.edit().putInt(KEY_FAILED_ATTEMPTS, value).apply()

    fun enable(pin: String) {
        val salt = ByteArray(SALT_BYTES).also { SecureRandom().nextBytes(it) }
        val hash = derive(pin, salt)
        val plain = JSONObject()
            .put("salt", Base64.encodeToString(salt, Base64.NO_WRAP))
            .put("hash", Base64.encodeToString(hash, Base64.NO_WRAP))
            .toString()
            .encodeToByteArray()
        val encryption = cipher(Cipher.ENCRYPT_MODE)
        val encrypted = encryption.doFinal(plain)
        prefs.edit()
            .putString(KEY_IV, Base64.encodeToString(encryption.iv, Base64.NO_WRAP))
            .putString(KEY_PAYLOAD, Base64.encodeToString(encrypted, Base64.NO_WRAP))
            .putBoolean(KEY_ENABLED, true)
            .putInt(KEY_FAILED_ATTEMPTS, 0)
            .apply()
    }

    fun verify(pin: String): Boolean {
        val stored = readSaltAndHash() ?: return false
        val (salt, hash) = stored
        val candidate = derive(pin, salt)
        return MessageDigest.isEqual(candidate, hash)
    }

    /** Возвращает `true`, если попыток стало больше [MAX_ATTEMPTS] (сигнал для wipe). */
    fun registerFailure(): Boolean {
        failedAttempts += 1
        return failedAttempts > MAX_ATTEMPTS
    }

    fun resetFailures() {
        failedAttempts = 0
    }

    fun disable() {
        prefs.edit().clear().apply()
    }

    private fun readSaltAndHash(): Pair<ByteArray, ByteArray>? = runCatching {
        val iv = prefs.getString(KEY_IV, null) ?: return null
        val payload = prefs.getString(KEY_PAYLOAD, null) ?: return null
        val plain = cipher(Cipher.DECRYPT_MODE, Base64.decode(iv, Base64.NO_WRAP))
            .doFinal(Base64.decode(payload, Base64.NO_WRAP))
        val json = JSONObject(plain.decodeToString())
        Base64.decode(json.getString("salt"), Base64.NO_WRAP) to Base64.decode(json.getString("hash"), Base64.NO_WRAP)
    }.getOrNull()

    private fun derive(pin: String, salt: ByteArray): ByteArray {
        val spec = PBEKeySpec(pin.toCharArray(), salt, PBKDF2_ITERATIONS, HASH_BYTES * 8)
        return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
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

    private companion object {
        const val PREFS_NAME = "barkcloud_app_lock"
        const val KEY_ENABLED = "enabled"
        const val KEY_FAILED_ATTEMPTS = "failed_attempts"
        const val KEY_IV = "iv"
        const val KEY_PAYLOAD = "payload"
        const val KEY_ALIAS = "BarkCloud.AppLock.v1"
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val GCM_TAG_BITS = 128
        const val SALT_BYTES = 16
        const val HASH_BYTES = 32
        const val PBKDF2_ITERATIONS = 100_000
        const val MAX_ATTEMPTS = 3
    }
}
