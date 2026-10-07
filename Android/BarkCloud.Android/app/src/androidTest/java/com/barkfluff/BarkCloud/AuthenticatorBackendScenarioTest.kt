package com.barkfluff.BarkCloud

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import barkcloud.identity.IdentityApiOuterClass.*
import com.barkfluff.BarkCloud.data.AuthResult
import com.barkfluff.BarkCloud.grpc.ServerSettings
import com.barkfluff.BarkCloud.ui.navigation.RootNavGraph
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.nio.ByteBuffer
import java.util.concurrent.TimeUnit
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Temporarily enables then removes TOTP on an explicitly supplied, dedicated test account. */
@RunWith(AndroidJUnit4::class)
class AuthenticatorBackendScenarioTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    @Test fun authenticatorLoginRejectsWrongCodeAndAcceptsCurrentCode() {
        val app = compose.activity.application as BarkCloudApplication
        val args = InstrumentationRegistry.getArguments()
        val username = args.getString("testUsername")
        val password = args.getString("testPassword")
        assumeTrue("Explicit permission to change a dedicated account's OTP is required", args.getString("testEnableAuthenticator") == "true" &&
            args.getString("testServerHost") == ServerSettings.host && username != null && password != null)
        // A successful password-only Auth also proves no existing OTP method is enabled.
        assertEquals(AuthResult.Success, runBlocking { app.authRepository.auth(username!!, password!!) })
        val setupToken = requireNotNull(app.globalParam.accessToken)
        val setup = app.grpcManager.registrationIdentityStub(setupToken).withDeadlineAfter(15, TimeUnit.SECONDS)
        val secret = runBlocking { setup.enableOtpVerification(EnableOtpVerificationRequest.newBuilder()
            .setOtpType(OtpTypeId.Authenticator).setPassword(password).build()).otpCode }
        var enabled = false
        try {
            runBlocking { setup.confirmOtpVerification(ConfirmOtpVerificationRequest.newBuilder().setOtpCode(totp(secret)).build()) }
            enabled = true
            // Keep the setup session solely for guaranteed cleanup if the UI login fails.
            runBlocking(Dispatchers.IO) { app.sessionManager.resetLocalState() }
            compose.setContent { BarkCloudTheme { RootNavGraph() } }
            compose.onNodeWithText("Почта или имя пользователя").performTextInput(username!!)
            compose.onNode(hasSetTextAction() and hasText("Пароль")).performTextInput(password!!)
            compose.onNodeWithText("Продолжить").performScrollTo().performClick()
            compose.waitUntil(25000) { compose.onAllNodesWithContentDescription("Код подтверждения, 6 цифр").fetchSemanticsNodes().isNotEmpty() }
            assertFalse(app.globalParam.sessionActive.value)
            val nearby = (-2..2).map { totp(secret, System.currentTimeMillis() / 30000 + it) }.toSet()
            val invalid = (0..999999).asSequence().map { it.toString().padStart(6, '0') }.first { it !in nearby }
            compose.onNode(hasSetTextAction()).performTextInput(invalid)
            compose.onNodeWithText("Войти").performScrollTo().performClick()
            compose.waitUntil(25000) { compose.onAllNodesWithText("Неверный код. Проверьте цифры и попробуйте снова.").fetchSemanticsNodes().isNotEmpty() }
            assertFalse(app.globalParam.sessionActive.value)
            compose.onNode(hasSetTextAction()).performTextReplacement(totp(secret))
            compose.onNodeWithText("Войти").performScrollTo().performClick()
            compose.waitUntil(25000) { app.globalParam.sessionActive.value }
            compose.onNodeWithText("Настройки").assertExists()
        } finally {
            // resetLocalState closed the setup channel; recreate it with the retained setup token.
            val cleanup = app.grpcManager.registrationIdentityStub(app.globalParam.accessToken ?: setupToken).withDeadlineAfter(15, TimeUnit.SECONDS)
            if (enabled) runBlocking { cleanup.disableOtpVerification(DisableOtpVerificationRequest.newBuilder()
                .setOtpType(OtpTypeId.Authenticator).setOtpCode(totp(secret)).build()) }
            runBlocking { runCatching { app.grpcManager.registrationIdentityStub(setupToken)
                .withDeadlineAfter(15, TimeUnit.SECONDS).logout(LogoutRequest.getDefaultInstance()) } }
            if (app.globalParam.sessionActive.value) runBlocking(Dispatchers.IO) { app.sessionManager.signOut() }
        }
    }

    private fun totp(secret: String, counter: Long = System.currentTimeMillis() / 30000): String {
        var buffer = 0
        var bits = 0
        val key = java.io.ByteArrayOutputStream()
        secret.trimEnd('=').uppercase().forEach { char ->
            val value = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".indexOf(char)
            require(value >= 0)
            buffer = (buffer shl 5) or value
            bits += 5
            if (bits >= 8) { bits -= 8; key.write((buffer shr bits) and 255) }
        }
        val mac = Mac.getInstance("HmacSHA1")
        mac.init(SecretKeySpec(key.toByteArray(), "HmacSHA1"))
        val hash = mac.doFinal(ByteBuffer.allocate(8).putLong(counter).array())
        val offset = hash.last().toInt() and 15
        val binary = ((hash[offset].toInt() and 127) shl 24) or ((hash[offset + 1].toInt() and 255) shl 16) or
            ((hash[offset + 2].toInt() and 255) shl 8) or (hash[offset + 3].toInt() and 255)
        return (binary % 1000000).toString().padStart(6, '0')
    }
}
