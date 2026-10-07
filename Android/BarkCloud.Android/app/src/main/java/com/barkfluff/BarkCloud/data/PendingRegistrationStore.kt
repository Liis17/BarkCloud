package com.barkfluff.BarkCloud.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class RegistrationDetails(val firstName: String, val lastName: String, val username: String, val email: String)
data class PendingRegistration(
    val serverKey: String,
    val details: RegistrationDetails,
    val refreshToken: String,
    val expiresAtMillis: Long,
)

interface PendingRegistrationStorage {
    fun read(): PendingRegistration?
    fun save(value: PendingRegistration)
    fun clear()
}

/** Separate encrypted record: never activates the application session. No password is persisted. */
class PendingRegistrationStore(context: Context) : PendingRegistrationStorage {
    private val prefs = context.getSharedPreferences("barkcloud_pending_registration", Context.MODE_PRIVATE)

    override fun read(): PendingRegistration? = runCatching {
        val payload = prefs.getString("payload", null) ?: return null
        val iv = prefs.getString("iv", null) ?: return null
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, Base64.decode(iv, Base64.NO_WRAP)))
        val json = JSONObject(cipher.doFinal(Base64.decode(payload, Base64.NO_WRAP)).decodeToString())
        PendingRegistration(
            json.getString("server"),
            RegistrationDetails(json.getString("first"), json.getString("last"), json.getString("username"), json.getString("email")),
            json.getString("refresh"), json.getLong("expires"),
        )
    }.getOrElse { clear(); null }

    override fun save(value: PendingRegistration) {
        val json = JSONObject().put("server", value.serverKey).put("refresh", value.refreshToken)
            .put("expires", value.expiresAtMillis).put("first", value.details.firstName)
            .put("last", value.details.lastName).put("username", value.details.username).put("email", value.details.email)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val payload = cipher.doFinal(json.toString().encodeToByteArray())
        check(prefs.edit().putString("iv", Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
            .putString("payload", Base64.encodeToString(payload, Base64.NO_WRAP)).commit()) {
            "Не удалось сохранить незавершённую регистрацию"
        }
    }

    override fun clear() { prefs.edit().clear().commit() }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        }.generateKey()
    }

    private companion object { const val KEY_ALIAS = "BarkCloud.PendingRegistration.v1" }
}
