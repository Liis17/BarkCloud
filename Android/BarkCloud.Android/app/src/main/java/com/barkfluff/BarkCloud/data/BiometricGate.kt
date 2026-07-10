package com.barkfluff.BarkCloud.data

import android.content.Context
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG
import androidx.biometric.BiometricManager.Authenticators.DEVICE_CREDENTIAL
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

enum class BiometricAvailability { AVAILABLE, NO_HARDWARE, NOT_ENROLLED, UNAVAILABLE }

/**
 * Общая обёртка над [BiometricPrompt] для App Lock и Vault (зеркалит iOS BiometricGate,
 * тонкую обёртку над LocalAuthentication). Допускает device PIN/паттерн как фолбэк
 * (аналог iOS `.deviceOwnerAuthentication`), поэтому не задаёт negative-button —
 * системная кнопка "Use screen lock" появляется автоматически при DEVICE_CREDENTIAL.
 */
object BiometricGate {
    private const val ALLOWED = BIOMETRIC_STRONG or DEVICE_CREDENTIAL

    fun availability(context: Context): BiometricAvailability =
        when (BiometricManager.from(context).canAuthenticate(ALLOWED)) {
            BiometricManager.BIOMETRIC_SUCCESS -> BiometricAvailability.AVAILABLE
            BiometricManager.BIOMETRIC_ERROR_NO_HARDWARE,
            BiometricManager.BIOMETRIC_ERROR_HW_UNAVAILABLE -> BiometricAvailability.NO_HARDWARE
            BiometricManager.BIOMETRIC_ERROR_NONE_ENROLLED -> BiometricAvailability.NOT_ENROLLED
            else -> BiometricAvailability.UNAVAILABLE
        }

    suspend fun authenticate(activity: FragmentActivity, title: String, subtitle: String? = null): Boolean =
        suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(
                activity,
                ContextCompat.getMainExecutor(activity),
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        if (continuation.isActive) continuation.resume(true)
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        if (continuation.isActive) continuation.resume(false)
                    }

                    override fun onAuthenticationFailed() {
                        // Один неудачный скан (напр. палец не распознан) — не завершаем поток,
                        // system prompt даёт повторить попытку сам.
                    }
                },
            )
            val info = BiometricPrompt.PromptInfo.Builder()
                .setTitle(title)
                .apply { subtitle?.let(::setSubtitle) }
                .setAllowedAuthenticators(ALLOWED)
                .build()
            continuation.invokeOnCancellation { prompt.cancelAuthentication() }
            prompt.authenticate(info)
        }
}
