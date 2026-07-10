package com.barkfluff.BarkCloud.ui.applock

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Backspace
import androidx.compose.material.icons.outlined.Fingerprint
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.BiometricAvailability
import com.barkfluff.BarkCloud.data.BiometricGate
import kotlinx.coroutines.launch

private const val PIN_LENGTH = 6

/** Full-screen блокировка приложения — авто-биометрия, PIN как fallback. Рисуется оверлеем поверх [com.barkfluff.BarkCloud.ui.navigation.RootNavGraph]. */
@Composable
fun AppLockScreen(onUnlocked: () -> Unit) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val activity = context as? FragmentActivity
    val scope = rememberCoroutineScope()

    var pin by remember { mutableStateOf("") }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var showPinEntry by remember { mutableStateOf(activity == null || BiometricGate.availability(context) != BiometricAvailability.AVAILABLE) }

    val unlockTitle = stringResource(R.string.applock_unlock_with_biometric)
    val lockedOutMessage = stringResource(R.string.applock_locked_out)
    val wrongPinMessage = stringResource(R.string.applock_wrong_pin)

    fun tryBiometric() {
        val fragmentActivity = activity ?: run { showPinEntry = true; return }
        scope.launch {
            val ok = BiometricGate.authenticate(fragmentActivity, unlockTitle)
            if (ok) {
                app.appLockStore.resetFailures()
                onUnlocked()
            } else {
                showPinEntry = true
            }
        }
    }

    LaunchedEffect(Unit) {
        if (!showPinEntry) tryBiometric()
    }

    fun submitPin(candidate: String) {
        if (app.appLockStore.verify(candidate)) {
            app.appLockStore.resetFailures()
            onUnlocked()
            return
        }
        pin = ""
        if (app.appLockStore.registerFailure()) {
            errorMessage = lockedOutMessage
            scope.launch {
                app.sessionManager.resetLocalState()
                app.appLockManager.unlock()
            }
        } else {
            errorMessage = wrongPinMessage
        }
    }

    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier.fillMaxSize().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Text(stringResource(R.string.applock_unlock_title), style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(32.dp))

            if (showPinEntry) {
                PinDots(length = pin.length, total = PIN_LENGTH)
                Spacer(Modifier.height(8.dp))
                errorMessage?.let {
                    Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium)
                }
                Spacer(Modifier.height(24.dp))
                PinKeypad(
                    onDigit = { digit ->
                        errorMessage = null
                        if (pin.length < PIN_LENGTH) {
                            val next = pin + digit
                            pin = next
                            if (next.length == PIN_LENGTH) submitPin(next)
                        }
                    },
                    onBackspace = { if (pin.isNotEmpty()) pin = pin.dropLast(1) },
                )
                if (activity != null && BiometricGate.availability(context) == BiometricAvailability.AVAILABLE) {
                    Spacer(Modifier.height(16.dp))
                    TextButton(onClick = { showPinEntry = false; tryBiometric() }) {
                        Text(unlockTitle)
                    }
                }
            } else {
                IconButton(onClick = ::tryBiometric, modifier = Modifier.size(72.dp)) {
                    Icon(Icons.Outlined.Fingerprint, contentDescription = unlockTitle, modifier = Modifier.size(48.dp))
                }
                Spacer(Modifier.height(16.dp))
                TextButton(onClick = { showPinEntry = true }) {
                    Text(stringResource(R.string.applock_use_pin))
                }
            }
        }
    }
}

@Composable
private fun PinDots(length: Int, total: Int) {
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        repeat(total) { index ->
            Box(
                modifier = Modifier
                    .size(14.dp)
                    .clip(CircleShape)
                    .background(
                        if (index < length) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant,
                    ),
            )
        }
    }
}

@Composable
private fun PinKeypad(onDigit: (Char) -> Unit, onBackspace: () -> Unit) {
    val rows = listOf("123", "456", "789")
    Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        rows.forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                row.forEach { digit -> KeypadDigit(digit) { onDigit(digit) } }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
            Spacer(Modifier.size(56.dp))
            KeypadDigit('0') { onDigit('0') }
            IconButton(onClick = onBackspace, modifier = Modifier.size(56.dp)) {
                Icon(Icons.AutoMirrored.Outlined.Backspace, contentDescription = null)
            }
        }
    }
}

@Composable
private fun KeypadDigit(digit: Char, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .size(56.dp)
            .clip(CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(digit.toString(), style = MaterialTheme.typography.headlineSmall)
    }
}
