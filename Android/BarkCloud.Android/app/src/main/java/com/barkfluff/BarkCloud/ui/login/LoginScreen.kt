package com.barkfluff.BarkCloud.ui.login

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.autofill.ContentType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.ui.auth.components.*

@Composable
fun LoginScreen(onAuthenticated: () -> Unit, server: String, onRegister: () -> Unit, onReset: () -> Unit,
    onServer: () -> Unit, onResumeRegistration: (() -> Unit)? = null,
    viewModel: LoginViewModel = viewModel(factory = LoginViewModel.factory())) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var passkey by remember { mutableStateOf(false) }
    val retry = deadlineSeconds(state.retryAtMillis)
    LaunchedEffect(viewModel) { viewModel.events.collect { onAuthenticated() } }
    BackHandler(state.otpRequired) { viewModel.backFromOtp() }
    AuthScaffold(if (state.otpRequired) "Подтвердите вход" else "Вход", if (state.otpRequired) viewModel::backFromOtp else null,
        kind = if (state.otpRequired) AuthDecorationKind.MAIL else AuthDecorationKind.PERSON, compactHeader = true,
        actions = {
            AuthError(state.credentialsError ?: state.snackbarMessage)
            if (retry > 0) Text("Повторить через $retry с", style = MaterialTheme.typography.bodySmall)
            if (!passkey || state.otpRequired) AuthPrimaryButton(if (state.otpRequired) "Войти" else "Продолжить", viewModel::submit,
                enabled = state.canSubmit && retry == 0L, loading = state.isLoading)
            else AuthPrimaryButton("Войти с паролем", { passkey = false })
            if (!state.otpRequired) {
                OutlinedButton(onRegister, Modifier.fillMaxWidth().heightIn(min = 56.dp), enabled = !state.isLoading) { Text("Создать аккаунт") }
                if (onResumeRegistration != null) TextButton(onResumeRegistration, enabled = !state.isLoading) { Text("Завершить регистрацию") }
                TextButton(onServer, enabled = !state.isLoading, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("Сменить сервер") }
            }
        }) {
        if (state.otpRequired) {
            Text("Введите код", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            AuthInfo("Введите шестизначный код подтверждения для аккаунта ${state.login}. Используйте код из приложения или письма, согласно настройкам аккаунта.")
            VerificationCodeField(state.otp, viewModel::onOtpChange, !state.isLoading, viewModel::submit)
            ResendCode(maxOf(state.resendAtMillis, state.retryAtMillis), !state.isLoading, viewModel::resendOtp)
        } else {
            Text(server, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                listOf("Пароль", "Ключ доступа").forEachIndexed { index, label ->
                    SegmentedButton(selected = passkey == (index == 1), onClick = { passkey = index == 1 }, enabled = !state.isLoading,
                        shape = SegmentedButtonDefaults.itemShape(index, 2)) { Text(label) }
                }
            }
            AnimatedContent(passkey, transitionSpec = { fadeIn(tween(200)) togetherWith fadeOut(tween(150)) }, label = "login method") { key ->
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    if (key) {
                        Text("Вход ключом доступа", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                        AuthInfo("Нативный вход ключом доступа пока недоступен в Android-клиенте. Используйте пароль для входа в аккаунт.")
                    } else {
                        AuthTextField(state.login, viewModel::onLoginChange, "Почта или имя пользователя", enabled = !state.isLoading, autofillType = ContentType.Username)
                        PasswordField(state.password, viewModel::onPasswordChange, enabled = !state.isLoading, onDone = viewModel::submit)
                        TextButton(onReset, enabled = !state.isLoading, modifier = Modifier.heightIn(min = 48.dp)) { Text("Забыли пароль?") }
                    }
                }
            }
        }
    }
}
