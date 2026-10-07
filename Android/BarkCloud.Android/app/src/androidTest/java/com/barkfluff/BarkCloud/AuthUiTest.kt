package com.barkfluff.BarkCloud

import androidx.activity.ComponentActivity
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.barkfluff.BarkCloud.data.*
import com.barkfluff.BarkCloud.grpc.*
import com.barkfluff.BarkCloud.ui.auth.*
import com.barkfluff.BarkCloud.ui.auth.components.*
import com.barkfluff.BarkCloud.ui.login.*
import com.barkfluff.BarkCloud.ui.onboarding.WelcomeScreen
import com.barkfluff.BarkCloud.ui.server.*
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AuthUiTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    private val app get() = compose.activity.application as BarkCloudApplication

    @Test fun welcomeRequiresAllThreePages() {
        var completed = false
        compose.setContent { BarkCloudTheme { WelcomeScreen { completed = true } } }
        compose.onNodeWithText("Далее").performClick()
        compose.onNodeWithText("Далее").performClick()
        compose.onNodeWithText("Сервер").performClick()
        compose.runOnIdle { assertTrue(completed) }
    }
    @Test fun verificationCodeUsesOneEditableNodeAndAcceptsPasteAndDeletion() {
        var code by mutableStateOf("")
        compose.setContent { BarkCloudTheme { VerificationCodeField(code, { code = it }) } }
        compose.onAllNodes(hasSetTextAction()).assertCountEquals(1)
        compose.onNode(hasSetTextAction()).performTextInput("12 3456789")
        compose.runOnIdle { assertEquals("123456", code) }
        compose.onNode(hasSetTextAction()).performTextReplacement("12345")
        compose.runOnIdle { assertEquals("12345", code) }
        compose.onNodeWithContentDescription("Код подтверждения, 6 цифр").assertExists()
    }
    @Test fun formActionsRemainReachableAtTwoHundredPercentFont() {
        var clicked = false
        compose.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 2f)) {
                BarkCloudTheme { AuthScaffold("Подтвердите регистрацию", actions = {
                    AuthPrimaryButton("Продолжить", { clicked = true })
                }) {
                    AuthInfo("Введите код подтверждения из письма. Формы прокручиваются вместе с действиями.")
                    VerificationCodeField("123456", {})
                } }
            }
        }
        compose.onNodeWithText("Продолжить").performScrollTo().assertIsDisplayed().performClick()
        compose.runOnIdle { assertTrue(clicked) }
    }
    @Test fun serverValidationDoesNotNavigateWithInvalidPort() {
        var connected = false
        val config = ServerConfig("https://example.com")
        val vm = ServerViewModel(config, config, ServerConnectionProbe { _, _, _ -> Result.success(Unit) }, {})
        compose.setContent { BarkCloudTheme { ServerScreen(vm, { connected = true }) } }
        compose.onNode(hasSetTextAction() and hasText("8000")).performTextReplacement("0")
        compose.onNodeWithText("Подключиться").performScrollTo().performClick()
        compose.onNodeWithText("Каждый порт должен быть числом от 1 до 65535").assertExists()
        compose.runOnIdle { assertFalse(connected) }
    }
    @Test fun passkeyExplainsAvailabilityAndReturnsToPassword() {
        val vm = LoginViewModel(app.authRepository)
        compose.setContent { BarkCloudTheme { LoginScreen({}, "example.com", {}, {}, {}, viewModel = vm) } }
        compose.onNodeWithText("Ключ доступа").performClick()
        compose.onNodeWithText("Нативный вход ключом доступа пока недоступен в Android-клиенте. Используйте пароль для входа в аккаунт.").assertExists()
        compose.onNodeWithText("Войти с паролем").performScrollTo().performClick()
        compose.onNodeWithText("Почта или имя пользователя").assertExists()
    }
    @Test fun registrationBackRetainsProfile() {
        val vm = RegistrationViewModel(app.authFlowRepository)
        compose.setContent { BarkCloudTheme { RegistrationScreen(vm, "example.com", {}, {}) } }
        compose.onNodeWithText("Имя").performTextInput("Алиса")
        compose.onNodeWithText("Далее").performScrollTo().performClick()
        compose.onNodeWithContentDescription("Назад").performScrollTo().performClick()
        compose.onNodeWithText("Алиса").assertExists()
    }
    @Test fun resetEmptyLoginShowsErrorAndPreservesDefaultRevocation() {
        val vm = ResetPasswordViewModel(app.authFlowRepository)
        compose.setContent { BarkCloudTheme { ResetPasswordScreen(vm, "example.com", {}, {}) } }
        compose.onNodeWithText("Отправить код").performScrollTo().performClick()
        compose.onNodeWithText("Введите логин или почту").assertExists()
        compose.runOnIdle { assertTrue(vm.state.value.revokeOtherSessions) }
    }
    @Test fun pendingRegistrationStoreSurvivesRecreationWithoutOpeningSession() {
        val store = PendingRegistrationStore(compose.activity)
        val pending = PendingRegistration(ServerSettings.config.key, RegistrationDetails("Алиса", "", "alice", "alice@example.com"), "pending-test-refresh", System.currentTimeMillis() + 60000)
        val before = app.globalParam.sessionActive.value
        try {
            store.save(pending)
            assertEquals(pending, PendingRegistrationStore(compose.activity).read())
            assertEquals(before, app.globalParam.sessionActive.value)
            val repo = AuthFlowRepository(app.grpcManager, app.globalParam, store)
            val vm = RegistrationViewModel(repo)
            compose.setContent { BarkCloudTheme { RegistrationScreen(vm, "example.com", {}, {}) } }
            compose.onNodeWithText("Пароль").assertExists()
            compose.runOnIdle { assertTrue(vm.state.value.pendingPassword); assertEquals("", vm.state.value.password) }
        } finally { store.clear() }
    }
    @Test fun passwordVisibilityHasAccessibleActionAndKeepsValue() {
        var password by mutableStateOf("abcdefgh")
        compose.setContent { BarkCloudTheme { PasswordField(password, { password = it }) } }
        compose.onNode(hasSetTextAction()).assert(SemanticsMatcher.keyIsDefined(androidx.compose.ui.semantics.SemanticsProperties.Password))
        compose.onNodeWithContentDescription("Показать пароль").performClick()
        compose.onNodeWithContentDescription("Скрыть пароль").assertExists()
        // KeyboardType.Password keeps the privacy semantics even while the characters are visible.
        compose.onNode(hasSetTextAction()).assertTextContains("abcdefgh")
        compose.runOnIdle { assertEquals("abcdefgh", password) }
    }

    @Test fun existingAppLockPinStillUnlocks() {
        org.junit.Assume.assumeFalse("Keep existing device PIN configuration", app.appLockStore.isEnabled)
        var unlocked = false
        try {
            app.appLockStore.enable("123456")
            compose.setContent { BarkCloudTheme { com.barkfluff.BarkCloud.ui.applock.AppLockScreen { unlocked = true } } }
            "123456".forEach { compose.onNodeWithText(it.toString()).performClick() }
            compose.runOnIdle { assertTrue(unlocked) }
        } finally { app.appLockStore.disable() }
    }

}
