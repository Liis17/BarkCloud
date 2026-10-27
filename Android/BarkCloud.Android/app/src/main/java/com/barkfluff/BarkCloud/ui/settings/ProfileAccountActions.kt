package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Logout
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.R

@Composable
internal fun ProfileAccountActions(
    enabled: Boolean,
    isProcessing: Boolean,
    onSignOut: () -> Unit,
    onDeleteAccount: () -> Unit,
) {
    // -1: sign out; 0: closed; 1..3: successive deletion warnings.
    var confirmation by rememberSaveable { mutableIntStateOf(0) }

    OutlinedButton(
        onClick = { confirmation = -1 },
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Icon(Icons.AutoMirrored.Outlined.Logout, contentDescription = null)
        Text(stringResource(R.string.settings_sign_out), modifier = Modifier.padding(start = 8.dp))
    }
    TextButton(
        onClick = { confirmation = 1 },
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Text(stringResource(R.string.settings_delete_account), color = MaterialTheme.colorScheme.error)
    }

    if (confirmation != 0) {
        val step = confirmation
        val deleting = step > 0
        val message = when (step) {
            1 -> R.string.settings_delete_account_message
            2 -> R.string.settings_delete_account_data_warning
            3 -> R.string.settings_delete_account_final_warning
            else -> R.string.settings_sign_out_message
        }
        AlertDialog(
            onDismissRequest = { confirmation = 0 },
            title = {
                Text(if (deleting) stringResource(R.string.settings_delete_account_title)
                    else stringResource(R.string.settings_sign_out_title))
            },
            text = { Text(stringResource(message)) },
            confirmButton = {
                TextButton(
                    enabled = enabled,
                    onClick = {
                        when (step) {
                            -1 -> { confirmation = 0; onSignOut() }
                            1, 2 -> confirmation = step + 1
                            3 -> { confirmation = 0; onDeleteAccount() }
                        }
                    },
                ) {
                    Text(
                        stringResource(when (step) {
                            -1 -> R.string.settings_sign_out
                            3 -> R.string.settings_delete_account_now
                            else -> R.string.settings_delete_account_continue
                        }),
                        color = if (deleting) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                    )
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmation = 0 }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }
    if (isProcessing) {
        AlertDialog(
            onDismissRequest = {},
            title = { Text(stringResource(R.string.settings_account_processing)) },
            text = { CircularProgressIndicator() },
            confirmButton = {},
        )
    }
}
