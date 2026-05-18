package com.barkfluff.BarkCloud.ui.login

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Visibility
import androidx.compose.material.icons.outlined.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R

@Composable
fun LoginScreen(
    onAuthenticated: () -> Unit,
    viewModel: LoginViewModel = viewModel(factory = LoginViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            when (event) {
                LoginViewModel.LoginEvent.NavigateToMain -> onAuthenticated()
            }
        }
    }

    LaunchedEffect(state.snackbarMessage) {
        val message = state.snackbarMessage ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(message)
        viewModel.snackbarShown()
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        containerColor = MaterialTheme.colorScheme.surface,
    ) { padding ->
        LoginContent(
            state = state,
            padding = padding,
            onLoginChange = viewModel::onLoginChange,
            onPasswordChange = viewModel::onPasswordChange,
            onPasswordVisibilityToggle = viewModel::onPasswordVisibilityToggle,
            onOtpChange = viewModel::onOtpChange,
            onSubmit = viewModel::submit,
            onComingSoon = viewModel::onComingSoon,
        )
    }
}

@Composable
private fun LoginContent(
    state: LoginUiState,
    padding: PaddingValues,
    onLoginChange: (String) -> Unit,
    onPasswordChange: (String) -> Unit,
    onPasswordVisibilityToggle: () -> Unit,
    onOtpChange: (String) -> Unit,
    onSubmit: () -> Unit,
    onComingSoon: () -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .padding(padding)
            .imePadding(),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text(
                text = stringResource(R.string.login_title),
                style = MaterialTheme.typography.displaySmall,
                color = MaterialTheme.colorScheme.onSurface,
            )

            Spacer(Modifier.heightIn(min = 8.dp))

            AnimatedContent(
                targetState = state.otpRequired,
                transitionSpec = {
                    val spec = spring<Float>(
                        dampingRatio = Spring.DampingRatioMediumBouncy,
                        stiffness = Spring.StiffnessLow,
                    )
                    (fadeIn(spec) + slideInVertically(
                        animationSpec = spring(
                            dampingRatio = Spring.DampingRatioMediumBouncy,
                            stiffness = Spring.StiffnessLow,
                        ),
                        initialOffsetY = { it / 4 },
                    )) togetherWith (fadeOut(spec) + slideOutVertically(
                        animationSpec = spring(
                            dampingRatio = Spring.DampingRatioNoBouncy,
                            stiffness = Spring.StiffnessLow,
                        ),
                        targetOffsetY = { -it / 4 },
                    ))
                },
                label = "login-mode",
            ) { otpMode ->
                if (otpMode) {
                    OtpField(
                        otp = state.otp,
                        enabled = !state.isLoading,
                        onChange = onOtpChange,
                    )
                } else {
                    CredentialsFields(
                        login = state.login,
                        password = state.password,
                        passwordVisible = state.passwordVisible,
                        credentialsError = state.credentialsError,
                        enabled = !state.isLoading,
                        onLoginChange = onLoginChange,
                        onPasswordChange = onPasswordChange,
                        onPasswordVisibilityToggle = onPasswordVisibilityToggle,
                        onImeSubmit = onSubmit,
                    )
                }
            }

            Button(
                onClick = onSubmit,
                enabled = state.canSubmit,
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 56.dp),
                shape = MaterialTheme.shapes.large,
            ) {
                if (state.isLoading) {
                    CircularProgressIndicator(
                        color = LocalContentColor.current,
                        strokeWidth = 2.dp,
                        modifier = Modifier.heightIn(min = 20.dp, max = 20.dp),
                    )
                } else {
                    Text(
                        text = stringResource(R.string.login_submit),
                        style = MaterialTheme.typography.labelLarge,
                    )
                }
            }

            Spacer(Modifier.heightIn(min = 8.dp))

            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier.fillMaxWidth(),
            ) {
                TextButton(onClick = onComingSoon, enabled = !state.isLoading) {
                    Text(text = stringResource(R.string.login_create_account))
                }
                TextButton(onClick = onComingSoon, enabled = !state.isLoading) {
                    Text(text = stringResource(R.string.login_forgot_password))
                }
            }
        }
    }
}

@Composable
private fun CredentialsFields(
    login: String,
    password: String,
    passwordVisible: Boolean,
    credentialsError: String?,
    enabled: Boolean,
    onLoginChange: (String) -> Unit,
    onPasswordChange: (String) -> Unit,
    onPasswordVisibilityToggle: () -> Unit,
    onImeSubmit: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        OutlinedTextField(
            value = login,
            onValueChange = onLoginChange,
            label = { Text(stringResource(R.string.login_field_label)) },
            singleLine = true,
            enabled = enabled,
            isError = credentialsError != null,
            keyboardOptions = KeyboardOptions(
                keyboardType = KeyboardType.Email,
                imeAction = ImeAction.Next,
            ),
            shape = MaterialTheme.shapes.medium,
            modifier = Modifier.fillMaxWidth(),
        )

        OutlinedTextField(
            value = password,
            onValueChange = onPasswordChange,
            label = { Text(stringResource(R.string.login_password_label)) },
            singleLine = true,
            enabled = enabled,
            isError = credentialsError != null,
            supportingText = credentialsError?.let { { Text(it) } },
            visualTransformation = if (passwordVisible) {
                VisualTransformation.None
            } else {
                PasswordVisualTransformation()
            },
            trailingIcon = {
                IconButton(onClick = onPasswordVisibilityToggle, enabled = enabled) {
                    val icon = if (passwordVisible) Icons.Outlined.VisibilityOff else Icons.Outlined.Visibility
                    val desc = stringResource(
                        if (passwordVisible) R.string.login_password_hide else R.string.login_password_show
                    )
                    Icon(imageVector = icon, contentDescription = desc)
                }
            },
            keyboardOptions = KeyboardOptions(
                keyboardType = KeyboardType.Password,
                imeAction = ImeAction.Done,
            ),
            shape = MaterialTheme.shapes.medium,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

@Composable
private fun OtpField(
    otp: String,
    enabled: Boolean,
    onChange: (String) -> Unit,
) {
    OutlinedTextField(
        value = otp,
        onValueChange = onChange,
        label = { Text(stringResource(R.string.login_otp_label)) },
        placeholder = { Text(stringResource(R.string.login_otp_hint)) },
        singleLine = true,
        enabled = enabled,
        keyboardOptions = KeyboardOptions(
            keyboardType = KeyboardType.NumberPassword,
            imeAction = ImeAction.Done,
        ),
        shape = MaterialTheme.shapes.medium,
        modifier = Modifier.fillMaxWidth(),
    )
}
