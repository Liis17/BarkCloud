package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.BiometricGate
import com.barkfluff.BarkCloud.ui.applock.PIN_LENGTH
import com.barkfluff.BarkCloud.ui.applock.PinDots
import com.barkfluff.BarkCloud.ui.applock.PinKeypad
import kotlinx.coroutines.launch

private sealed interface PinFlowStep {
    data object Enter : PinFlowStep
    data class Confirm(val firstPin: String) : PinFlowStep
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppLockSettingsScreen(
    onNavigateUp: () -> Unit,
    viewModel: AppLockSettingsViewModel = viewModel(factory = AppLockSettingsViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    val scope = rememberCoroutineScope()
    var pinFlow by remember { mutableStateOf<PinFlowStep?>(null) }

    val confirmTitle = stringResource(R.string.applock_unlock_with_biometric)
    val pinMismatchMessage = stringResource(R.string.applock_pin_mismatch)
    val enableFailedMessage = stringResource(R.string.applock_enable_failed)

    LaunchedEffect(state.snackbar) {
        state.snackbar?.let { snackbarHostState.showSnackbar(it); viewModel.snackbarShown() }
    }

    fun confirmIdentity(onConfirmed: () -> Unit) {
        val fragmentActivity = activity
        if (fragmentActivity == null) {
            scope.launch { snackbarHostState.showSnackbar(enableFailedMessage) }
            return
        }
        scope.launch {
            val ok = BiometricGate.authenticate(fragmentActivity, confirmTitle)
            if (ok) onConfirmed() else scope.launch { snackbarHostState.showSnackbar(enableFailedMessage) }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.applock_title)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Column(
            modifier = Modifier.fillMaxSize().padding(padding).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            ElevatedCard(Modifier.fillMaxWidth()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        stringResource(R.string.applock_enable),
                        style = MaterialTheme.typography.bodyLarge,
                        modifier = Modifier.weight(1f),
                    )
                    Switch(
                        checked = state.isEnabled,
                        onCheckedChange = { checked ->
                            confirmIdentity {
                                if (checked) {
                                    pinFlow = PinFlowStep.Enter
                                } else {
                                    viewModel.disable()
                                }
                            }
                        },
                    )
                }
            }
        }
    }

    pinFlow?.let { step ->
        PinEntryDialog(
            title = when (step) {
                is PinFlowStep.Enter -> stringResource(R.string.applock_setup_pin_title)
                is PinFlowStep.Confirm -> stringResource(R.string.applock_confirm_pin_title)
            },
            onComplete = { pin ->
                when (step) {
                    is PinFlowStep.Enter -> pinFlow = PinFlowStep.Confirm(pin)
                    is PinFlowStep.Confirm -> {
                        if (pin == step.firstPin) {
                            viewModel.enable(pin)
                            pinFlow = null
                        } else {
                            scope.launch { snackbarHostState.showSnackbar(pinMismatchMessage) }
                            pinFlow = PinFlowStep.Enter
                        }
                    }
                }
            },
            onDismiss = { pinFlow = null },
        )
    }
}

@Composable
private fun PinEntryDialog(title: String, onComplete: (String) -> Unit, onDismiss: () -> Unit) {
    var pin by remember(title) { mutableStateOf("") }
    Dialog(onDismissRequest = onDismiss) {
        Surface(shape = MaterialTheme.shapes.large) {
            Column(
                modifier = Modifier.padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(16.dp))
                PinDots(length = pin.length, total = PIN_LENGTH)
                Spacer(Modifier.height(24.dp))
                PinKeypad(
                    onDigit = { digit ->
                        if (pin.length < PIN_LENGTH) {
                            val next = pin + digit
                            pin = next
                            if (next.length == PIN_LENGTH) onComplete(next)
                        }
                    },
                    onBackspace = { if (pin.isNotEmpty()) pin = pin.dropLast(1) },
                )
            }
        }
    }
}
