package com.barkfluff.BarkCloud.ui.settings

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Logout
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material.icons.outlined.CloudUpload
import androidx.compose.material.icons.outlined.Devices
import androidx.compose.material.icons.outlined.Edit
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.Storage
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.formatSize
import com.barkfluff.BarkCloud.ui.components.RemoteImage

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    onEditProfile: () -> Unit,
    onPrivacy: () -> Unit,
    onDevices: () -> Unit,
    onUploadSettings: () -> Unit,
    onCache: () -> Unit,
    onSignedOut: () -> Unit,
    viewModel: ProfileViewModel = viewModel(factory = ProfileViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current

    var showSignOut by remember { mutableStateOf(false) }
    var showDelete by remember { mutableStateOf(false) }

    val avatarPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia(),
    ) { uri -> uri?.let(viewModel::setAvatar) }

    LaunchedEffectOnce { viewModel.load() }

    androidx.compose.runtime.LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            when (event) {
                ProfileViewModel.ProfileEvent.SignedOut -> onSignedOut()
            }
        }
    }

    androidx.compose.runtime.LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = { CenterAlignedTopAppBar(title = { Text(stringResource(R.string.settings_title)) }) },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                ProfileHeader(
                    state = state,
                    onPickAvatar = {
                        avatarPicker.launch(
                            PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                        )
                    },
                    onRemoveAvatar = viewModel::removeAvatar,
                )

                if (state.storageLimit > 0) {
                    StorageCard(used = state.usedStorage, limit = state.storageLimit)
                }

                ElevatedCard(Modifier.fillMaxWidth()) {
                    SettingsRow(Icons.Outlined.Edit, stringResource(R.string.settings_edit_profile), onEditProfile)
                    HorizontalDivider()
                    SettingsRow(Icons.Outlined.Lock, stringResource(R.string.settings_privacy), onPrivacy)
                    HorizontalDivider()
                    SettingsRow(Icons.Outlined.Devices, stringResource(R.string.settings_devices), onDevices)
                    HorizontalDivider()
                    SettingsRow(Icons.Outlined.CloudUpload, "Загрузки", onUploadSettings)
                    HorizontalDivider()
                    SettingsRow(Icons.Outlined.Storage, stringResource(R.string.cache_title), onCache)
                }

                OutlinedButton(
                    onClick = { showSignOut = true },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(Icons.AutoMirrored.Outlined.Logout, contentDescription = null)
                    Text(
                        text = stringResource(R.string.settings_sign_out),
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }

                TextButton(
                    onClick = { showDelete = true },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(
                        text = stringResource(R.string.settings_delete_account),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }

            if (state.isProcessing) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color.Black.copy(alpha = 0.3f)),
                    contentAlignment = Alignment.Center,
                ) {
                    CircularProgressIndicator()
                }
            }
        }
    }

    if (showSignOut) {
        ConfirmDialog(
            title = stringResource(R.string.settings_sign_out_title),
            message = stringResource(R.string.settings_sign_out_message),
            confirmText = stringResource(R.string.settings_sign_out),
            onConfirm = { showSignOut = false; viewModel.signOut() },
            onDismiss = { showSignOut = false },
        )
    }
    if (showDelete) {
        ConfirmDialog(
            title = stringResource(R.string.settings_delete_account_title),
            message = stringResource(R.string.settings_delete_account_message),
            confirmText = stringResource(R.string.common_delete),
            destructive = true,
            onConfirm = { showDelete = false; viewModel.deleteAccount() },
            onDismiss = { showDelete = false },
        )
    }
}

@Composable
private fun ProfileHeader(
    state: ProfileUiState,
    onPickAvatar: () -> Unit,
    onRemoveAvatar: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Box(
            modifier = Modifier
                .size(96.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.surfaceVariant)
                .clickable(onClick = onPickAvatar),
            contentAlignment = Alignment.Center,
        ) {
            if (state.avatarUrl != null) {
                RemoteImage(
                    url = state.avatarUrl,
                    contentDescription = null,
                    normalize = true,
                    modifier = Modifier.fillMaxSize(),
                )
            } else {
                Icon(
                    Icons.Outlined.Person,
                    contentDescription = null,
                    modifier = Modifier.size(48.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (state.isUpdatingAvatar) {
                Box(
                    Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.4f)),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(28.dp)) }
            } else {
                Box(
                    Modifier.fillMaxSize(),
                    contentAlignment = Alignment.BottomEnd,
                ) {
                    Surface(color = MaterialTheme.colorScheme.primary, shape = CircleShape) {
                        Icon(
                            Icons.Filled.PhotoCamera,
                            contentDescription = stringResource(R.string.settings_avatar_change),
                            tint = MaterialTheme.colorScheme.onPrimary,
                            modifier = Modifier.padding(4.dp).size(16.dp),
                        )
                    }
                }
            }
        }

        Text(state.displayName, style = MaterialTheme.typography.headlineSmall)
        if (state.username.isNotEmpty()) {
            Text("@${state.username}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (state.bio.isNotEmpty()) {
            Text(state.bio, style = MaterialTheme.typography.bodyMedium, textAlign = TextAlign.Center)
        }
        if (state.hasAvatar) {
            TextButton(onClick = onRemoveAvatar, enabled = !state.isUpdatingAvatar) {
                Text(stringResource(R.string.settings_avatar_remove))
            }
        }
    }
}

@Composable
private fun StorageCard(used: Long, limit: Long) {
    val context = LocalContext.current
    ElevatedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.settings_storage_title), style = MaterialTheme.typography.titleMedium)
            LinearProgressIndicator(
                progress = { if (limit > 0) (used.toFloat() / limit).coerceIn(0f, 1f) else 0f },
                modifier = Modifier.fillMaxWidth(),
            )
            Text(
                text = stringResource(R.string.settings_storage_usage, formatSize(context, used), formatSize(context, limit)),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun SettingsRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    onClick: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(title) },
        leadingContent = { Icon(icon, contentDescription = null) },
        trailingContent = { Icon(Icons.Outlined.ChevronRight, contentDescription = null) },
        modifier = Modifier.clickable(onClick = onClick),
    )
}

@Composable
private fun ConfirmDialog(
    title: String,
    message: String,
    confirmText: String,
    destructive: Boolean = false,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(message) },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(
                    confirmText,
                    color = if (destructive) MaterialTheme.colorScheme.error else Color.Unspecified,
                )
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
        },
    )
}

@Composable
private fun LaunchedEffectOnce(block: () -> Unit) {
    androidx.compose.runtime.LaunchedEffect(Unit) { block() }
}
