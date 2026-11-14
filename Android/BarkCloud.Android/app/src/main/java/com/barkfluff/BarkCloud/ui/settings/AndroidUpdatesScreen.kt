package com.barkfluff.BarkCloud.ui.settings

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.InstallMobile
import androidx.compose.material.icons.outlined.NewReleases
import androidx.compose.material.icons.outlined.PauseCircle
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Science
import androidx.compose.material.icons.outlined.Storage
import androidx.compose.material.icons.outlined.Sync
import androidx.compose.material.icons.outlined.SystemUpdate
import androidx.compose.material.icons.outlined.Update
import androidx.compose.material.icons.outlined.VerifiedUser
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.update.AndroidUpdatePhase
import com.barkfluff.BarkCloud.data.update.AndroidUpdateUiState
import com.barkfluff.BarkCloud.files.ui.formatSize
import java.time.Instant
import java.time.ZoneId
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AndroidUpdatesScreen(
    onNavigateUp: () -> Unit,
    viewModel: AndroidUpdatesViewModel = viewModel(factory = AndroidUpdatesViewModel.factory()),
) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val lifecycleOwner = LocalLifecycleOwner.current
    val state by viewModel.state.collectAsStateWithLifecycle()
    val actionError by viewModel.actionError.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    var showDeleteConfirmation by remember { mutableStateOf(false) }
    val unknownSourcesSettings = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        viewModel.onResume()
    }

    LaunchedEffect(viewModel) { viewModel.onResume() }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { viewModel.onResume() }
    LaunchedEffect(state.phase, lifecycleOwner) {
        if (state.phase !in setOf(AndroidUpdatePhase.DOWNLOADING, AndroidUpdatePhase.WAITING, AndroidUpdatePhase.VERIFYING)) {
            return@LaunchedEffect
        }
        lifecycleOwner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            while (isActive) {
                delay(DOWNLOAD_POLL_MILLIS)
                viewModel.reconcileNow()
            }
        }
    }

    Scaffold(
        contentWindowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.android_updates_title)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = stringResource(R.string.files_back))
                    }
                },
                actions = {
                    IconButton(onClick = viewModel::manualRefresh, enabled = state.phase != AndroidUpdatePhase.CHECKING) {
                        Icon(Icons.Outlined.Refresh, contentDescription = stringResource(R.string.android_updates_check))
                    }
                },
                windowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
            )
        },
    ) { padding ->
        val installUpdate: () -> Unit = {
            viewModel.clearActionError()
            scope.launch {
                try {
                    if (!app.androidUpdateRepository.canRequestInstallPackages()) {
                        unknownSourcesSettings.launch(
                            Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                                .setData(Uri.parse("package:${context.packageName}")),
                        )
                    } else {
                        val apk = viewModel.prepareInstall()
                        val uri = FileProvider.getUriForFile(
                            context,
                            "${context.packageName}.fileprovider",
                            apk,
                        )
                        context.startActivity(
                            Intent(Intent.ACTION_INSTALL_PACKAGE)
                                .setDataAndType(uri, "application/vnd.android.package-archive")
                                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
                        )
                    }
                } catch (error: Exception) {
                    if (error is CancellationException) throw error
                    viewModel.reportActionError(error.message ?: context.getString(R.string.android_updates_install_error))
                }
            }
        }
        val release = state.release?.takeIf { state.latestUnavailableTag == null }
        val notes = remember(release?.notes) { release?.notes?.let(::parseReleaseNotes).orEmpty() }

        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            UpdateStatusCard(
                state = state,
                actionError = actionError,
                onDownload = viewModel::download,
                onCancel = viewModel::cancelDownload,
                onInstall = installUpdate,
                onDelete = { showDeleteConfirmation = true },
            )

            UpdatesSection(stringResource(R.string.android_updates_section_versions)) {
                VersionsCard(state)
            }

            UpdatesSection(stringResource(R.string.android_updates_section_settings)) {
                ElevatedCard(Modifier.fillMaxWidth()) {
                    val enabled = state.phase != AndroidUpdatePhase.CHECKING
                    ListItem(
                        headlineContent = { Text(stringResource(R.string.android_updates_beta_title)) },
                        supportingContent = { Text(stringResource(R.string.android_updates_beta_description)) },
                        leadingContent = { Icon(Icons.Outlined.Science, contentDescription = null) },
                        trailingContent = { Switch(checked = state.betaChannel, onCheckedChange = null, enabled = enabled) },
                        modifier = Modifier.toggleable(
                            value = state.betaChannel,
                            enabled = enabled,
                            role = Role.Switch,
                            onValueChange = viewModel::setBetaChannel,
                        ),
                    )
                }
            }

            if (release != null && notes.isNotEmpty()) {
                UpdatesSection(stringResource(R.string.android_updates_notes)) {
                    ElevatedCard(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                                Icon(Icons.Outlined.NewReleases, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                                UpdateBadge(release.tag, UpdateBadgeKind.VERSION)
                            }
                            HorizontalDivider()
                            ReleaseNotesContent(notes)
                        }
                    }
                }
            }
        }
    }

    if (showDeleteConfirmation) {
        AlertDialog(
            onDismissRequest = { showDeleteConfirmation = false },
            title = { Text(stringResource(R.string.android_updates_delete_title)) },
            text = { Text(stringResource(R.string.android_updates_delete_message)) },
            confirmButton = {
                TextButton(onClick = {
                    showDeleteConfirmation = false
                    viewModel.deleteDownloadedUpdate()
                }) { Text(stringResource(R.string.android_updates_delete)) }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteConfirmation = false }) { Text(stringResource(R.string.android_updates_keep)) }
            },
        )
    }
}

@Composable
private fun UpdatesSection(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            title,
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.padding(horizontal = 4.dp),
        )
        content()
    }
}

/** Главная карточка: состояние, переход версий, прогресс, ошибки и действия текущей фазы. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun UpdateStatusCard(
    state: AndroidUpdateUiState,
    actionError: String?,
    onDownload: () -> Unit,
    onCancel: () -> Unit,
    onInstall: () -> Unit,
    onDelete: () -> Unit,
) {
    val context = LocalContext.current
    val visuals = phaseVisuals(state.phase)
    val release = state.release?.takeIf { state.latestUnavailableTag == null }
    ElevatedCard(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.extraLarge,
        colors = CardDefaults.elevatedCardColors(containerColor = visuals.container, contentColor = visuals.content),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Box(
                    Modifier
                        .size(48.dp)
                        .background(visuals.accent, CircleShape),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(visuals.icon, contentDescription = null, tint = visuals.onAccent)
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(phaseTitle(state.phase), style = MaterialTheme.typography.titleLarge)
                    Text(
                        phaseMessage(state),
                        style = MaterialTheme.typography.bodyMedium,
                        color = LocalContentColor.current.copy(alpha = SECONDARY_TEXT_ALPHA),
                    )
                }
            }

            if (release != null && state.phase in TRANSITION_PHASES) {
                VersionTransition(from = state.currentVersionName, to = release.versionName)
            }

            when (state.phase) {
                AndroidUpdatePhase.DOWNLOADING, AndroidUpdatePhase.WAITING -> {
                    val total = state.totalBytes.takeIf { it > 0 } ?: release?.apkSize ?: 0
                    if (total > 0) {
                        val progress = (state.downloadedBytes.toFloat() / total.toFloat()).coerceIn(0f, 1f)
                        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            LinearWavyProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    stringResource(
                                        R.string.android_updates_progress,
                                        formatSize(context, state.downloadedBytes),
                                        formatSize(context, total),
                                    ),
                                    style = MaterialTheme.typography.bodySmall,
                                    modifier = Modifier.weight(1f),
                                )
                                Text(
                                    stringResource(R.string.android_updates_percent, (progress * 100).toInt()),
                                    style = MaterialTheme.typography.labelLarge,
                                )
                            }
                        }
                    } else {
                        LinearWavyProgressIndicator(modifier = Modifier.fillMaxWidth())
                    }
                }
                AndroidUpdatePhase.CHECKING, AndroidUpdatePhase.VERIFYING ->
                    LinearWavyProgressIndicator(modifier = Modifier.fillMaxWidth())
                else -> Unit
            }

            listOfNotNull(state.message, actionError).forEach { message -> StatusError(message) }
            state.retryAtMillis?.takeIf { state.message != null }?.let { retryAt ->
                Text(
                    stringResource(R.string.android_updates_retry_after, formatRetryAt(retryAt)),
                    style = MaterialTheme.typography.bodySmall,
                    color = LocalContentColor.current.copy(alpha = SECONDARY_TEXT_ALPHA),
                )
            }

            when (state.phase) {
                AndroidUpdatePhase.DOWNLOADING, AndroidUpdatePhase.WAITING -> FilledTonalButton(
                    onClick = onCancel,
                    modifier = Modifier.fillMaxWidth(),
                ) { ButtonContent(Icons.Outlined.Close, stringResource(R.string.android_updates_cancel)) }
                AndroidUpdatePhase.READY -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onInstall, modifier = Modifier.fillMaxWidth()) {
                        ButtonContent(Icons.Outlined.InstallMobile, stringResource(R.string.android_updates_install))
                    }
                    OutlinedButton(onClick = onDelete, modifier = Modifier.fillMaxWidth()) {
                        ButtonContent(Icons.Outlined.DeleteOutline, stringResource(R.string.android_updates_delete))
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Icon(Icons.Outlined.Info, contentDescription = null, modifier = Modifier.size(16.dp).padding(top = 1.dp))
                        Text(
                            stringResource(R.string.android_updates_install_warning),
                            style = MaterialTheme.typography.bodySmall,
                            color = LocalContentColor.current.copy(alpha = SECONDARY_TEXT_ALPHA),
                        )
                    }
                }
                AndroidUpdatePhase.AVAILABLE, AndroidUpdatePhase.FAILED -> Button(
                    onClick = onDownload,
                    enabled = state.release != null,
                    modifier = Modifier.fillMaxWidth(),
                ) { ButtonContent(Icons.Outlined.Download, stringResource(R.string.android_updates_download)) }
                else -> Unit
            }
        }
    }
}

@Composable
private fun ButtonContent(icon: ImageVector, text: String) {
    Icon(icon, contentDescription = null, modifier = Modifier.size(ButtonDefaults.IconSize))
    Spacer(Modifier.width(ButtonDefaults.IconSpacing))
    Text(text)
}

@Composable
private fun StatusError(message: String) {
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
        shape = MaterialTheme.shapes.medium,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Icon(Icons.Outlined.ErrorOutline, contentDescription = null, modifier = Modifier.size(20.dp))
            Text(message, style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun VersionsCard(state: AndroidUpdateUiState) {
    val context = LocalContext.current
    val release = state.release?.takeIf { state.latestUnavailableTag == null }
    ElevatedCard(Modifier.fillMaxWidth()) {
        VersionRow(
            icon = Icons.Outlined.PhoneAndroid,
            label = stringResource(R.string.android_updates_installed),
            version = state.currentVersionName,
            kind = UpdateBadgeKind.NEUTRAL,
            versionCode = state.currentVersionCode,
        )
        state.latestUnavailableTag?.let { tag ->
            HorizontalDivider()
            VersionRow(
                icon = Icons.Outlined.NewReleases,
                label = stringResource(R.string.android_updates_latest_release),
                version = tag,
                kind = UpdateBadgeKind.VERSION,
                versionCode = null,
            )
        }
        release?.let {
            HorizontalDivider()
            VersionRow(
                icon = Icons.Outlined.NewReleases,
                label = stringResource(R.string.android_updates_latest),
                version = it.versionName,
                kind = UpdateBadgeKind.VERSION,
                versionCode = it.versionCode,
            )
            HorizontalDivider()
            ListItem(
                headlineContent = { Text(stringResource(R.string.android_updates_size)) },
                leadingContent = { Icon(Icons.Outlined.Storage, contentDescription = null) },
                trailingContent = { Text(formatSize(context, it.apkSize), style = MaterialTheme.typography.labelLarge) },
            )
        }
    }
}

@Composable
private fun VersionRow(icon: ImageVector, label: String, version: String, kind: UpdateBadgeKind, versionCode: Long?) {
    ListItem(
        overlineContent = { Text(label) },
        headlineContent = { UpdateBadge(version, kind, Modifier.padding(vertical = 4.dp)) },
        supportingContent = versionCode?.let { code ->
            { Text(stringResource(R.string.android_updates_version_code, code)) }
        },
        leadingContent = { Icon(icon, contentDescription = null) },
    )
}

private data class PhaseVisuals(
    val icon: ImageVector,
    val container: Color,
    val content: Color,
    val accent: Color,
    val onAccent: Color,
)

@Composable
private fun phaseVisuals(phase: AndroidUpdatePhase): PhaseVisuals {
    val scheme = MaterialTheme.colorScheme
    return when (phase) {
        AndroidUpdatePhase.AVAILABLE -> PhaseVisuals(Icons.Outlined.SystemUpdate, scheme.primaryContainer, scheme.onPrimaryContainer, scheme.primary, scheme.onPrimary)
        AndroidUpdatePhase.READY -> PhaseVisuals(Icons.Outlined.InstallMobile, scheme.primaryContainer, scheme.onPrimaryContainer, scheme.primary, scheme.onPrimary)
        AndroidUpdatePhase.FAILED -> PhaseVisuals(Icons.Outlined.ErrorOutline, scheme.errorContainer, scheme.onErrorContainer, scheme.error, scheme.onError)
        AndroidUpdatePhase.CHECKING -> PhaseVisuals(Icons.Outlined.Sync, scheme.secondaryContainer, scheme.onSecondaryContainer, scheme.secondary, scheme.onSecondary)
        AndroidUpdatePhase.DOWNLOADING -> PhaseVisuals(Icons.Outlined.Download, scheme.secondaryContainer, scheme.onSecondaryContainer, scheme.secondary, scheme.onSecondary)
        AndroidUpdatePhase.WAITING -> PhaseVisuals(Icons.Outlined.PauseCircle, scheme.secondaryContainer, scheme.onSecondaryContainer, scheme.secondary, scheme.onSecondary)
        AndroidUpdatePhase.VERIFYING -> PhaseVisuals(Icons.Outlined.VerifiedUser, scheme.secondaryContainer, scheme.onSecondaryContainer, scheme.secondary, scheme.onSecondary)
        AndroidUpdatePhase.UP_TO_DATE -> PhaseVisuals(Icons.Outlined.CheckCircle, scheme.surfaceContainerHigh, scheme.onSurface, scheme.tertiaryContainer, scheme.onTertiaryContainer)
        AndroidUpdatePhase.UNAVAILABLE_METADATA -> PhaseVisuals(Icons.Outlined.Info, scheme.surfaceContainerHigh, scheme.onSurface, scheme.tertiaryContainer, scheme.onTertiaryContainer)
        AndroidUpdatePhase.IDLE -> PhaseVisuals(Icons.Outlined.Update, scheme.surfaceContainerHigh, scheme.onSurface, scheme.secondaryContainer, scheme.onSecondaryContainer)
    }
}

@Composable
private fun phaseTitle(phase: AndroidUpdatePhase): String = stringResource(
    when (phase) {
        AndroidUpdatePhase.IDLE -> R.string.android_updates_title_idle
        AndroidUpdatePhase.CHECKING -> R.string.android_updates_title_checking
        AndroidUpdatePhase.AVAILABLE -> R.string.android_updates_title_available
        AndroidUpdatePhase.UP_TO_DATE -> R.string.android_updates_title_up_to_date
        AndroidUpdatePhase.UNAVAILABLE_METADATA -> R.string.android_updates_title_unavailable_metadata
        AndroidUpdatePhase.DOWNLOADING -> R.string.android_updates_title_downloading
        AndroidUpdatePhase.WAITING -> R.string.android_updates_title_waiting
        AndroidUpdatePhase.VERIFYING -> R.string.android_updates_title_verifying
        AndroidUpdatePhase.READY -> R.string.android_updates_title_ready
        AndroidUpdatePhase.FAILED -> R.string.android_updates_title_failed
    },
)

@Composable
private fun phaseMessage(state: AndroidUpdateUiState): String = when (state.phase) {
    AndroidUpdatePhase.IDLE -> stringResource(R.string.android_updates_idle)
    AndroidUpdatePhase.CHECKING -> stringResource(R.string.android_updates_checking)
    AndroidUpdatePhase.AVAILABLE -> stringResource(R.string.android_updates_available)
    AndroidUpdatePhase.UP_TO_DATE -> stringResource(R.string.android_updates_up_to_date)
    AndroidUpdatePhase.UNAVAILABLE_METADATA -> stringResource(R.string.android_updates_unavailable_metadata)
    AndroidUpdatePhase.DOWNLOADING -> stringResource(R.string.android_updates_downloading)
    AndroidUpdatePhase.WAITING -> stringResource(R.string.android_updates_waiting)
    AndroidUpdatePhase.VERIFYING -> stringResource(R.string.android_updates_verifying)
    AndroidUpdatePhase.READY -> stringResource(R.string.android_updates_ready)
    AndroidUpdatePhase.FAILED -> stringResource(R.string.android_updates_failed)
}

private fun formatRetryAt(timestamp: Long): String =
    Instant.ofEpochMilli(timestamp).atZone(ZoneId.systemDefault()).toLocalDateTime().toString()

private const val DOWNLOAD_POLL_MILLIS = 1_000L
private const val SECONDARY_TEXT_ALPHA = 0.8f
private val TRANSITION_PHASES = setOf(
    AndroidUpdatePhase.AVAILABLE,
    AndroidUpdatePhase.DOWNLOADING,
    AndroidUpdatePhase.WAITING,
    AndroidUpdatePhase.VERIFYING,
    AndroidUpdatePhase.READY,
    AndroidUpdatePhase.FAILED,
)
