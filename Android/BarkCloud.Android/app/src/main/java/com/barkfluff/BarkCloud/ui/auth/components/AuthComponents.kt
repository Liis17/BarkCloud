package com.barkfluff.BarkCloud.ui.auth.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Visibility
import androidx.compose.material.icons.outlined.VisibilityOff
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.*
import androidx.compose.ui.autofill.ContentType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.barkfluff.BarkCloud.data.verificationDigits
import kotlinx.coroutines.delay

private val LocalAuthFocus = staticCompositionLocalOf<(Any, Boolean) -> Unit> { { _, _ -> } }

@Composable
private fun Modifier.authInputFocus(): Modifier {
    val key = remember { Any() }
    val report = LocalAuthFocus.current
    DisposableEffect(key, report) { onDispose { report(key, false) } }
    return onFocusChanged { report(key, it.isFocused) }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun AuthScaffold(
    title: String,
    onBack: (() -> Unit)? = null,
    step: String? = null,
    kind: AuthDecorationKind = AuthDecorationKind.PERSON,
    compactHeader: Boolean = false,
    actions: @Composable ColumnScope.() -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    val motion = rememberAuthMotionAllowed()
    val keyboard = WindowInsets.isImeVisible
    val focus = remember { mutableStateMapOf<Any, Boolean>() }
    val report = remember { { key: Any, focused: Boolean -> if (focused) focus[key] = true else focus.remove(key); Unit } }
    BoxWithConstraints(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface)
        .windowInsetsPadding(WindowInsets.safeDrawing).imePadding()) {
        val availableHeight = maxHeight
        val heroHeight = if (maxHeight < 600.dp || keyboard) 156.dp else if (compactHeader) 180.dp else 236.dp
        CompositionLocalProvider(LocalAuthFocus provides report, LocalContentColor provides MaterialTheme.colorScheme.onSurface) {
            Column(Modifier.widthIn(max = 480.dp).fillMaxWidth().align(Alignment.TopCenter)
                .verticalScroll(rememberScrollState())) {
                AuthHero(title, step, kind, heroHeight, onBack, motion && !keyboard && focus.isEmpty())
                Column(Modifier.fillMaxWidth().heightIn(min = (availableHeight - heroHeight).coerceAtLeast(260.dp))
                    .padding(24.dp), verticalArrangement = Arrangement.SpaceBetween) {
                    Column(verticalArrangement = Arrangement.spacedBy(16.dp), content = content)
                    Column(Modifier.padding(top = 28.dp), verticalArrangement = Arrangement.spacedBy(12.dp), content = actions)
                }
            }
        }
    }
}

@Composable
fun AuthPrimaryButton(text: String, onClick: () -> Unit, enabled: Boolean = true, loading: Boolean = false, large: Boolean = false) {
    val interactions = remember { MutableInteractionSource() }
    val pressed by interactions.collectIsPressedAsState()
    val scale = animateFloatAsState(if (pressed && !loading) .98f else 1f, tween(120), label = "button press")
    Button(onClick, enabled = enabled && !loading, interactionSource = interactions,
        modifier = Modifier.fillMaxWidth().heightIn(min = if (large) 64.dp else 56.dp)
            .graphicsLayer { scaleX = scale.value; scaleY = scale.value },
        shape = RoundedCornerShape(if (large) 32.dp else 28.dp), contentPadding = PaddingValues(16.dp)) {
        if (loading) {
            CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(12.dp))
        }
        Text(text, fontSize = if (large) 18.sp else 16.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
fun AuthTextField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    keyboardType: KeyboardType = KeyboardType.Text,
    autofillType: ContentType? = null,
    onDone: (() -> Unit)? = null,
    prefix: String? = null,
) {
    OutlinedTextField(value, onValueChange, enabled = enabled, label = { Text(label) }, singleLine = true,
        shape = RoundedCornerShape(16.dp),
        prefix = prefix?.let { { Text(it) } },
        modifier = modifier.fillMaxWidth().authInputFocus().then(if (autofillType != null) Modifier.semantics { contentType = autofillType } else Modifier),
        keyboardOptions = KeyboardOptions(keyboardType = keyboardType, imeAction = if (onDone != null) ImeAction.Done else ImeAction.Next),
        keyboardActions = KeyboardActions(onDone = { onDone?.invoke() }))
}

@Composable
fun PasswordField(value: String, onValueChange: (String) -> Unit, label: String = "Пароль", enabled: Boolean = true,
    newPassword: Boolean = false, onDone: (() -> Unit)? = null) {
    var visible by remember { mutableStateOf(false) }
    OutlinedTextField(value, onValueChange, enabled = enabled, label = { Text(label) }, singleLine = true,
        shape = RoundedCornerShape(16.dp),
        modifier = Modifier.fillMaxWidth().authInputFocus().semantics { contentType = if (newPassword) ContentType.NewPassword else ContentType.Password },
        visualTransformation = if (visible) VisualTransformation.None else PasswordVisualTransformation(),
        trailingIcon = { IconButton(onClick = { visible = !visible }, enabled = enabled) {
            Icon(if (visible) Icons.Outlined.VisibilityOff else Icons.Outlined.Visibility,
                contentDescription = if (visible) "Скрыть пароль" else "Показать пароль")
        } },
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = if (onDone != null) ImeAction.Done else ImeAction.Next),
        keyboardActions = KeyboardActions(onDone = { onDone?.invoke() }))
}

@Composable
fun PasswordStrength(password: String) {
    val groups = listOf(password.any(Char::isLowerCase), password.any(Char::isUpperCase), password.any(Char::isDigit),
        password.any { !it.isLetterOrDigit() }).count { it }
    val score = if (password.isEmpty()) 0 else listOf(password.length >= 8, password.length >= 12, password.length >= 16, groups >= 3).count { it }.coerceAtLeast(1)
    Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.clearAndSetSemantics {
        if (password.isNotEmpty()) contentDescription = "Надёжность пароля: ${listOf("", "низкая", "средняя", "хорошая", "высокая")[score]}"
    }) {
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            repeat(4) { index ->
                val color by androidx.compose.animation.animateColorAsState(
                    if (index < score) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceContainerHighest,
                    tween(180), label = "password strength")
                Box(Modifier.weight(1f).height(4.dp).background(color, RoundedCornerShape(2.dp)))
            }
        }
        Text(if (password.isEmpty()) "Минимум 8 символов" else "Надёжность: ${listOf("", "низкая", "средняя", "хорошая", "высокая")[score]}",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun VerificationCodeField(value: String, onValueChange: (String) -> Unit, enabled: Boolean = true, onDone: (() -> Unit)? = null) {
    var focused by remember { mutableStateOf(false) }
    BasicTextField(value, { onValueChange(verificationDigits(it)) }, enabled = enabled, singleLine = true,
        textStyle = MaterialTheme.typography.headlineSmall.copy(color = Color.Transparent),
        cursorBrush = SolidColor(Color.Transparent),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { onDone?.invoke() }),
        modifier = Modifier.fillMaxWidth().height(60.dp).authInputFocus().onFocusChanged { focused = it.isFocused }
            .semantics { contentDescription = "Код подтверждения, 6 цифр"; contentType = ContentType.SmsOtpCode },
        decorationBox = { inner ->
            Box {
                Row(Modifier.fillMaxSize().clearAndSetSemantics {}, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    repeat(6) { index ->
                        val selected = focused && index == value.length.coerceAtMost(5)
                        val border by androidx.compose.animation.animateColorAsState(
                            if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, tween(150), label = "code focus")
                        Surface(Modifier.weight(1f).fillMaxHeight(), color = MaterialTheme.colorScheme.surface,
                            shape = RoundedCornerShape(14.dp), border = androidx.compose.foundation.BorderStroke(if (selected) 2.dp else 1.dp, border)) {
                            Box(contentAlignment = Alignment.Center) {
                                Text(value.getOrNull(index)?.toString().orEmpty(), style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                            }
                        }
                    }
                }
                Box(Modifier.fillMaxSize().alpha(0f)) { inner() }
            }
        })
}

@Composable
fun AuthError(message: String?) {
    AnimatedVisibility(message != null, enter = fadeIn(tween(150)) + expandVertically(), exit = fadeOut(tween(100)) + shrinkVertically()) {
        Surface(color = MaterialTheme.colorScheme.errorContainer, shape = RoundedCornerShape(16.dp), modifier = Modifier.fillMaxWidth()
            .semantics { liveRegion = LiveRegionMode.Polite }) {
            Text(message.orEmpty(), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onErrorContainer, style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
fun AuthInfo(text: String) {
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = RoundedCornerShape(20.dp)) {
        Text(text, Modifier.fillMaxWidth().padding(16.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun deadlineSeconds(deadline: Long): Long {
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val seconds by produceState(initialValue = ((deadline - System.currentTimeMillis() + 999) / 1000).coerceAtLeast(0), deadline, lifecycle) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                value = ((deadline - System.currentTimeMillis() + 999) / 1000).coerceAtLeast(0)
                if (value == 0L) break
                delay(1000)
            }
        }
    }
    return seconds
}

@Composable
fun ResendCode(deadline: Long, enabled: Boolean, onResend: () -> Unit) {
    val seconds = deadlineSeconds(deadline)
    TextButton(onClick = onResend, enabled = enabled && seconds == 0L, modifier = Modifier.heightIn(min = 48.dp)) {
        Text(if (seconds > 0) "Отправить снова через ${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}" else "Отправить код снова")
    }
}
