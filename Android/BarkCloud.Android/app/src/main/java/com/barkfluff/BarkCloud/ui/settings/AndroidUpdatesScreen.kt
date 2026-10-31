package com.barkfluff.BarkCloud.ui.settings

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
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
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.android_updates_installed), style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(R.string.android_updates_current_version, state.currentVersionName, state.currentVersionCode))
                    state.latestUnavailableTag?.let { tag ->
                        Text(stringResource(R.string.android_updates_latest_tag, tag))
                    }
                    state.release?.takeIf { state.latestUnavailableTag == null }?.let { release ->
                        Text(stringResource(R.string.android_updates_latest_version, release.versionName, release.versionCode))
                        Text(stringResource(R.string.android_updates_size, formatSize(context, release.apkSize)))
                    }
                    Text(phaseMessage(state), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    state.retryAtMillis?.takeIf { state.message != null }?.let { retryAt ->
                        Text(
                            stringResource(R.string.android_updates_retry_after, formatRetryAt(retryAt)),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            state.message?.let {
                Text(it, color = MaterialTheme.colorScheme.error)
            }
            actionError?.let { message ->
                Text(message, color = MaterialTheme.colorScheme.error)
            }

            if (state.phase == AndroidUpdatePhase.CHECKING) {
                CircularProgressIndicator()
            }
            if (state.phase == AndroidUpdatePhase.DOWNLOADING || state.phase == AndroidUpdatePhase.WAITING) {
                val total = state.totalBytes.takeIf { it > 0 } ?: state.release?.apkSize ?: 0
                if (total > 0) {
                    LinearProgressIndicator(
                        progress = { (state.downloadedBytes.toFloat() / total.toFloat()).coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Text(stringResource(R.string.android_updates_progress, formatSize(context, state.downloadedBytes), formatSize(context, total)))
                } else {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            }

            when (state.phase) {
                AndroidUpdatePhase.DOWNLOADING, AndroidUpdatePhase.WAITING -> Button(
                    onClick = viewModel::cancelDownload,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text(stringResource(R.string.android_updates_cancel)) }
                AndroidUpdatePhase.VERIFYING -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    CircularProgressIndicator()
                    Text(stringResource(R.string.android_updates_verifying), modifier = Modifier.padding(top = 10.dp))
                }
                AndroidUpdatePhase.READY -> {
                    Button(
                        onClick = {
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
                        },
                        modifier = Modifier.fillMaxWidth(),
                    ) { Text(stringResource(R.string.android_updates_install)) }
                    OutlinedButton(
                        onClick = { showDeleteConfirmation = true },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Icon(Icons.Outlined.DeleteOutline, contentDescription = null)
                        Text(stringResource(R.string.android_updates_delete))
                    }
                    Text(stringResource(R.string.android_updates_install_warning), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                AndroidUpdatePhase.AVAILABLE, AndroidUpdatePhase.FAILED -> Button(
                    onClick = viewModel::download,
                    enabled = state.release != null,
                    modifier = Modifier.fillMaxWidth(),
                ) { Text(stringResource(R.string.android_updates_download)) }
                else -> Unit
            }

            state.release?.takeIf { state.latestUnavailableTag == null }?.notes?.takeIf(String::isNotBlank)?.let { notes ->
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(stringResource(R.string.android_updates_notes), style = MaterialTheme.typography.titleMedium)
                        Text(notes)
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
