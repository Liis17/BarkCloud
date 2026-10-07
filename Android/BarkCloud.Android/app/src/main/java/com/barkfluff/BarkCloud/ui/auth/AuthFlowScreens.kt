package com.barkfluff.BarkCloud.ui.auth

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.autofill.ContentType
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.barkfluff.BarkCloud.ui.auth.components.*
import com.barkfluff.BarkCloud.ui.navigation.sharedAxisXStep

@Composable
fun RegistrationScreen(viewModel: RegistrationViewModel, server: String, onBack: () -> Unit, onAuthenticated: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val back = { if (!viewModel.back()) onBack() }
    BackHandler { back() }
    val retry = deadlineSeconds(state.retryAt)
    AnimatedContent(state.step, transitionSpec = {
        sharedAxisXStep(targetState > initialState)
    }, label = "registration step") { step ->
        if (step == 4) AuthSuccessScreen(state.firstName, state.username, server, onAuthenticated)
        else AuthScaffold(
            title = if (state.pendingPassword) "Завершите\nрегистрацию" else listOf("Как вас\nзовут?", "Имя\nпользователя", "Почта\nи пароль", "Подтвердите\nпочту")[step],
            onBack = back, step = if (state.pendingPassword) null else "Шаг ${step + 1} из 4",
            kind = listOf(AuthDecorationKind.PERSON, AuthDecorationKind.USERNAME, AuthDecorationKind.PASSWORD, AuthDecorationKind.MAIL)[step],
            actions = {
                AuthError(state.error)
                if (retry > 0) Text("Повторить через $retry с", style = MaterialTheme.typography.bodySmall)
                AuthPrimaryButton(if (state.pendingPassword) "Установить пароль" else if (step == 3) "Создать аккаунт" else "Далее",
                    viewModel::next, enabled = retry == 0L, loading = state.loading)
                TextButton(onClick = back, enabled = !state.loading, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                    Text(if (state.pendingPassword) "Перейти ко входу" else "Назад")
                }
            },
        ) {
            when (step) {
                0 -> {
                    Text("Так вас увидят другие пользователи сервера.", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    AuthTextField(state.firstName, viewModel::firstNameChanged, "Имя", enabled = !state.loading, autofillType = ContentType.PersonFirstName)
                    AuthTextField(state.lastName, viewModel::lastNameChanged, "Фамилия (необязательно)", enabled = !state.loading, autofillType = ContentType.PersonLastName, onDone = viewModel::next)
                }
                1 -> {
                    Text("Уникальное имя для входа и упоминаний. Его можно изменить в настройках.", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    AuthTextField(state.username, viewModel::usernameChanged, "Юзернейм", enabled = !state.loading,
                        autofillType = ContentType.NewUsername, prefix = "@", onDone = viewModel::next)
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite }) {
                        if (state.checkingUsername) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        else if (state.usernameAvailable == true) Icon(Icons.Outlined.CheckCircle, null, tint = MaterialTheme.colorScheme.primary)
                        Text(when { state.checkingUsername -> "Проверяем имя…"; state.usernameAvailable == true -> "Имя свободно"; state.usernameAvailable == false -> "Имя занято"; else -> "Латиница, цифры, точка и подчёркивание" },
                            style = MaterialTheme.typography.bodySmall, color = if (state.usernameAvailable == false) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    if (!state.checkingUsername && state.usernameAvailable == null && state.error != null) {
                        TextButton(onClick = { viewModel.usernameChanged(state.username) }, enabled = retry == 0L) { Text("Проверить ещё раз") }
                    }
                }
                2 -> {
                    if (state.pendingPassword) AuthInfo("Аккаунт подтверждён. Установите пароль, чтобы завершить регистрацию. Пароль не сохраняется на устройстве.")
                    else AuthTextField(state.email, viewModel::emailChanged, "Электронная почта", enabled = !state.loading,
                        keyboardType = KeyboardType.Email, autofillType = ContentType.EmailAddress)
                    PasswordField(state.password, viewModel::passwordChanged, enabled = !state.loading, newPassword = true, onDone = viewModel::next)
                    PasswordStrength(state.password)
                }
                3 -> {
                    Text("Введите 6-значный код из письма, которое мы отправили на ${state.email}.", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    VerificationCodeField(state.code, viewModel::codeChanged, enabled = !state.loading, onDone = viewModel::next)
                    ResendCode(maxOf(state.resendAt, state.retryAt), !state.loading, viewModel::resend)
                    TextButton(onClick = back, enabled = !state.loading) { Text("Изменить почту") }
                }
            }
        }
    }
}

@Composable
fun ResetPasswordScreen(viewModel: ResetPasswordViewModel, server: String, onBack: () -> Unit, onAuthenticated: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    LaunchedEffect(viewModel) { viewModel.events.collect { onAuthenticated() } }
    val back = { if (!viewModel.back()) onBack() }
    BackHandler { back() }
    val retry = deadlineSeconds(state.retryAt)
    AnimatedContent(state.step, transitionSpec = {
        sharedAxisXStep(targetState > initialState)
    }, label = "reset step") { step ->
        AuthScaffold(listOf("Сброс\nпароля", "Проверьте\nпочту", "Новый\nпароль")[step], back,
            step = "Шаг ${step + 1} из 3", kind = listOf(AuthDecorationKind.RESET, AuthDecorationKind.MAIL, AuthDecorationKind.PASSWORD)[step],
            actions = {
                AuthError(state.error)
                if (retry > 0) Text("Повторить через $retry с", style = MaterialTheme.typography.bodySmall)
                AuthPrimaryButton(listOf("Отправить код", "Далее", "Сохранить")[step], viewModel::next, retry == 0L, state.loading)
                TextButton(onClick = back, enabled = !state.loading, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("Назад") }
            }) {
            when (step) {
                0 -> {
                    Text("Укажите логин или почту, привязанную к аккаунту.", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    AuthTextField(state.login, viewModel::loginChanged, "Логин или почта", enabled = !state.loading,
                        autofillType = ContentType.Username + ContentType.EmailAddress, onDone = viewModel::next)
                    Text("Сервер: $server", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    AuthInfo("Если письма нет во входящих, проверьте папку «Спам».")
                }
                1 -> {
                    Text("Если аккаунт существует, письмо с кодом отправлено на привязанную почту.", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    VerificationCodeField(state.code, viewModel::codeChanged, !state.loading, viewModel::next)
                    ResendCode(maxOf(state.resendAt, state.retryAt), !state.loading, viewModel::resend)
                }
                2 -> {
                    PasswordField(state.password, viewModel::passwordChanged, "Новый пароль", !state.loading, true)
                    PasswordStrength(state.password)
                    PasswordField(state.repeatedPassword, viewModel::repeatChanged, "Повторите пароль", !state.loading, true, viewModel::next)
                    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = MaterialTheme.shapes.large) {
                        Row(Modifier.fillMaxWidth().toggleable(state.revokeOtherSessions, enabled = !state.loading, role = Role.Checkbox,
                            onValueChange = viewModel::revokeChanged).padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(state.revokeOtherSessions, onCheckedChange = null, enabled = !state.loading)
                            Spacer(Modifier.width(12.dp))
                            Text("Выйти на остальных устройствах", style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                    AuthInfo("Код будет проверен при сохранении нового пароля.")
                }
            }
        }
    }
}
