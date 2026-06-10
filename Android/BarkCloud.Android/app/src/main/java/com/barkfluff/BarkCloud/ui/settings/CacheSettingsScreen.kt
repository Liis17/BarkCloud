package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cache.FileCacheSettings
import com.barkfluff.BarkCloud.files.ui.formatSize

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CacheSettingsScreen(
    onNavigateUp: () -> Unit,
    viewModel: CacheSettingsViewModel = viewModel(factory = CacheSettingsViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current
    var confirmClearAll by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.cache_title)) },
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
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            ElevatedCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.cache_size), style = MaterialTheme.typography.titleMedium)
                    LinearProgressIndicator(
                        progress = {
                            if (state.maxCacheBytes > 0) {
                                (state.cacheSize.toFloat() / state.maxCacheBytes).coerceIn(0f, 1f)
                            } else {
                                0f
                            }
                        },
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Text(
                        stringResource(
                            R.string.cache_size_summary,
                            formatSize(context, state.cacheSize),
                            formatSize(context, state.maxCacheBytes),
                            state.entryCount,
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            ElevatedCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.cache_limit), style = MaterialTheme.typography.titleMedium)
                    ChipRow(
                        options = cacheLimitOptions(),
                        selected = state.maxCacheBytes,
                        onSelect = viewModel::setMaxCacheBytes,
                        label = { formatSize(context, it) },
                    )
                }
            }

            ElevatedCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.cache_auto_clean), style = MaterialTheme.typography.titleMedium)
                    ChipRow(
                        options = staleOptions(),
                        selected = state.staleMaxAgeMillis,
                        onSelect = viewModel::setStaleMaxAge,
                        label = { staleLabel(it) },
                    )
                }
            }

            OutlinedButton(onClick = viewModel::clearStale, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.cache_clear_stale))
            }
            Button(onClick = { confirmClearAll = true }, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.cache_clear_all))
            }
        }
    }

    if (confirmClearAll) {
        AlertDialog(
            onDismissRequest = { confirmClearAll = false },
            title = { Text(stringResource(R.string.cache_clear_confirm_title)) },
            text = { Text(stringResource(R.string.cache_clear_confirm_message)) },
            confirmButton = {
                TextButton(onClick = { confirmClearAll = false; viewModel.clearAll() }) {
                    Text(stringResource(R.string.cache_clear_all))
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmClearAll = false }) {
                    Text(stringResource(R.string.common_cancel))
                }
            },
        )
    }
}

@Composable
private fun ChipRow(
    options: List<Long>,
    selected: Long,
    onSelect: (Long) -> Unit,
    label: @Composable (Long) -> String,
) {
    androidx.compose.foundation.layout.FlowRow(
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        options.forEach { value ->
            AssistChip(
                onClick = { onSelect(value) },
                label = { Text(label(value)) },
                enabled = value != selected,
            )
        }
    }
}

private fun cacheLimitOptions(): List<Long> =
    listOf(1, 2, 5, 10, 20).map { it * 1024L * 1024L * 1024L }

private fun staleOptions(): List<Long> =
    listOf(
        FileCacheSettings.DAY_MILLIS,
        7L * FileCacheSettings.DAY_MILLIS,
        30L * FileCacheSettings.DAY_MILLIS,
        FileCacheSettings.NEVER,
    )

@Composable
private fun staleLabel(value: Long): String = when (value) {
    FileCacheSettings.DAY_MILLIS -> stringResource(R.string.cache_auto_clean_1d)
    7L * FileCacheSettings.DAY_MILLIS -> stringResource(R.string.cache_auto_clean_7d)
    30L * FileCacheSettings.DAY_MILLIS -> stringResource(R.string.cache_auto_clean_30d)
    else -> stringResource(R.string.cache_auto_clean_never)
}
