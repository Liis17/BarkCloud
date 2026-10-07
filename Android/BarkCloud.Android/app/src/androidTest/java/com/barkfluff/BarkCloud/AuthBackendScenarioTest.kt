package com.barkfluff.BarkCloud

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.grpc.ServerSettings
import com.barkfluff.BarkCloud.ui.navigation.RootNavGraph
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** Explicit opt-in only. Supply a dedicated account through runner arguments; never log credentials. */
@RunWith(AndroidJUnit4::class)
class AuthBackendScenarioTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    private val app get() = compose.activity.application as BarkCloudApplication

    @Test fun temporaryAuthenticatorWasRemoved() = runBlocking {
        val args = InstrumentationRegistry.getArguments()
        val username = args.getString("testUsername")
        val password = args.getString("testPassword")
        assumeTrue("Dedicated account and matching server required", args.getString("testServerHost") == ServerSettings.host && username != null && password != null)
        try {
            org.junit.Assert.assertEquals(com.barkfluff.BarkCloud.data.AuthResult.Success, app.authRepository.auth(username!!, password!!))
            val methods = app.grpcManager.identityStub().withDeadlineAfter(15, java.util.concurrent.TimeUnit.SECONDS)
                .listOtpVerification(barkcloud.identity.IdentityApiOuterClass.ListOtpVerificationRequest.getDefaultInstance())
            assertFalse(methods.authenticatorEnabled)
        } finally {
            if (app.globalParam.sessionActive.value) app.sessionManager.signOut()
        }
    }

    @Test fun passwordLoginOpensMainAndSignOutReturnsToSavedServer() {
        val args = InstrumentationRegistry.getArguments()
        val host = args.getString("testServerHost")
        val username = args.getString("testUsername")
        val password = args.getString("testPassword")
        assumeTrue("Dedicated account and matching server required", host != null && host == ServerSettings.host && username != null && password != null)
        runBlocking(Dispatchers.IO) { app.sessionManager.resetLocalState() }
        compose.setContent { BarkCloudTheme { RootNavGraph() } }
        try {
            compose.onNodeWithText("Почта или имя пользователя").performTextInput(username!!)
            compose.onNode(hasSetTextAction() and hasText("Пароль")).performTextInput(password!!)
            compose.onNodeWithText("Продолжить").performScrollTo().performClick()
            val networkError = "Не удалось связаться с сервером. Проверьте подключение и повторите."
            compose.waitUntil(25000) { app.globalParam.sessionActive.value || compose.onAllNodesWithText(networkError).fetchSemanticsNodes().isNotEmpty() }
            if (!app.globalParam.sessionActive.value) {
                compose.onNodeWithText(networkError).assertExists()
                compose.onNodeWithText("Продолжить").performScrollTo().assertIsEnabled().performClick()
                compose.waitUntil(25000) { app.globalParam.sessionActive.value }
            }
            compose.onNodeWithText("Настройки").assertExists()
            val profile = runBlocking(Dispatchers.IO) {
                app.grpcManager.usersStub().withDeadlineAfter(15, java.util.concurrent.TimeUnit.SECONDS)
                    .getUser(barkcloud.users.UsersApiOuterClass.GetUserRequest.getDefaultInstance()).user
            }
            org.junit.Assert.assertEquals(username, profile.username)
            runBlocking(Dispatchers.IO) { app.sessionManager.signOut() }
            compose.waitUntil(10000) { compose.onAllNodesWithText("Почта или имя пользователя").fetchSemanticsNodes().isNotEmpty() }
            assertFalse(app.globalParam.sessionActive.value)
            org.junit.Assert.assertEquals(host, ServerSettings.host)
        } finally {
            if (app.globalParam.sessionActive.value) runBlocking(Dispatchers.IO) { app.sessionManager.signOut() }
        }
    }

    @Test fun restoredSessionSupportsSettingsDeepLinkAndSignOut() {
        val args = InstrumentationRegistry.getArguments()
        val host = args.getString("testServerHost")
        val username = args.getString("testRestoredUsername")
        assumeTrue("Explicit restored account and matching server required", host != null && host == ServerSettings.host && username != null)
        org.junit.Assert.assertTrue(app.globalParam.hasValidRefreshToken())
        org.junit.Assert.assertNull(app.authFlowRepository.pendingRegistration())
        compose.setContent { BarkCloudTheme { RootNavGraph(android.net.Uri.parse("barkcloud://settings")) } }
        try {
            val profile = runBlocking(Dispatchers.IO) {
                app.grpcManager.usersStub().withDeadlineAfter(15, java.util.concurrent.TimeUnit.SECONDS)
                    .getUser(barkcloud.users.UsersApiOuterClass.GetUserRequest.getDefaultInstance()).user
            }
            org.junit.Assert.assertEquals(username, profile.username)
            compose.onNodeWithText("Редактировать профиль").assertExists()
            runBlocking(Dispatchers.IO) { app.sessionManager.signOut() }
            compose.waitUntil(10000) { compose.onAllNodesWithText("Почта или имя пользователя").fetchSemanticsNodes().isNotEmpty() }
        } finally {
            if (app.globalParam.sessionActive.value) runBlocking(Dispatchers.IO) { app.sessionManager.signOut() }
        }
    }
}
