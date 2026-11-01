package com.barkfluff.BarkCloud.ui.upload

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Cancel
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase

@Composable
fun UploadQueueScreen(
    onNavigateUp: () -> Unit,
    viewModel: UploadQueueViewModel = viewModel(factory = UploadQueueViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    UploadQueueContent(
        state = state,
        onNavigateUp = onNavigateUp,
        onRetry = { viewModel.retry(it) },
        onCancel = { viewModel.cancel(it) },
    )
}

@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)
@Composable
internal fun UploadQueueContent(
    state: UploadQueueUiState,
    onNavigateUp: () -> Unit,
    onRetry: (String) -> Unit,
    onCancel: (String) -> Unit,
    modifier: Modifier = Modifier,
    gridState: LazyGridState = rememberLazyGridState(),
) {
    var selectedJobId by remember { mutableStateOf<String?>(null) }
    val selectedJob = state.pendingJobs.firstOrNull { it.id == selectedJobId }
    val currentPhasesById = remember(state.pendingJobs) { state.pendingJobs.associate { it.id to it.phase } }
    val currentIndicesById = remember(state.pendingJobs) {
        state.pendingJobs.mapIndexed { index, job -> job.id to index }.toMap()
    }
    var previousPhasesById by remember { mutableStateOf(currentPhasesById) }

    SideEffect {
        if (previousPhasesById != currentPhasesById && state.pendingJobs.isNotEmpty()) {
            if (gridState.firstVisibleItemIndex == 0 && gridState.firstVisibleItemScrollOffset == 0) {
                gridState.requestScrollToItem(0)
            } else {
                val anchor = gridState.layoutInfo.visibleItemsInfo.firstOrNull { item ->
                    val id = item.key as? String
                    id != null && currentPhasesById.containsKey(id) && previousPhasesById[id] == currentPhasesById[id]
                }
                val anchorIndex = anchor?.key?.let { currentIndicesById[it] } ?: -1
                if (anchorIndex >= 0 && anchor != null) {
                    gridState.requestScrollToItem(anchorIndex, -anchor.offset.y)
                } else {
                    gridState.requestScrollToItem(
                        gridState.firstVisibleItemIndex.coerceAtMost(state.pendingJobs.lastIndex),
                        gridState.firstVisibleItemScrollOffset.coerceAtMost(gridState.layoutInfo.viewportSize.height),
                    )
                }
            }
        }
        previousPhasesById = currentPhasesById
    }

    Scaffold(
        modifier = modifier,
        topBar = {
            TopAppBar(
                title = {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            stringResource(R.string.gallery_uploads_title),
                            modifier = Modifier.weight(1f),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        if (state.pendingErrorCount > 0) {
                            Text(
                                stringResource(R.string.gallery_upload_error_count, state.pendingErrorCount),
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.error,
                                maxLines = 1,
                            )
                        }
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(
                            Icons.AutoMirrored.Outlined.ArrowBack,
                            contentDescription = stringResource(R.string.upload_queue_back),
                        )
                    }
                },
            )
        },
    ) { padding ->
        if (state.pendingJobs.isEmpty()) {
            Box(
                modifier = Modifier.fillMaxSize().padding(padding).padding(24.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    stringResource(R.string.gallery_uploads_empty),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        } else {
            LazyVerticalGrid(
                columns = GridCells.Fixed(3),
                state = gridState,
                modifier = Modifier.fillMaxSize().padding(padding).testTag("upload-queue-grid"),
                contentPadding = PaddingValues(8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                items(
                    items = state.pendingJobs,
                    key = { job -> job.id },
                ) { job ->
                    UploadJobTile(
                        job = job,
                        modifier = Modifier.animateItem(),
                        onClick = { selectedJobId = job.id },
                    )
                }
            }
        }
    }

    selectedJob?.let { job ->
        UploadJobDetailsSheet(
            job = job,
            onDismiss = { selectedJobId = null },
            onRetry = { onRetry(job.id); selectedJobId = null },
            onCancel = { onCancel(job.id); selectedJobId = null },
        )
    }
}

@Composable
private fun UploadJobTile(
    job: UploadJob,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val label = stringResource(R.string.gallery_upload_task_accessibility, job.fileName, job.phase.label())
    Card(
        onClick = onClick,
        modifier = modifier.fillMaxWidth().semantics(mergeDescendants = true) {
            contentDescription = label
        },
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(8.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            UploadJobThumbnail(
                job = job,
                modifier = Modifier.fillMaxWidth().aspectRatio(1f),
            )
            Text(
                job.fileName,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelMedium,
            )
            Text(
                job.phase.label(),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
                color = if (job.phase == UploadPhase.FAILED || job.phase == UploadPhase.UPLOADED_NOT_ATTACHED) {
                    MaterialTheme.colorScheme.error
                } else {
                    MaterialTheme.colorScheme.onSurfaceVariant
                },
            )
            UploadJobProgressIndicator(job)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun UploadJobDetailsSheet(
    job: UploadJob,
    onDismiss: () -> Unit,
    onRetry: () -> Unit,
    onCancel: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 24.dp)
                .padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(job.fileName, style = MaterialTheme.typography.titleLarge)
            Text(
                job.phase.label(),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            job.errorMessage?.takeIf(String::isNotBlank)?.let { error ->
                Text(error, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            when {
                job.phase == UploadPhase.FAILED || job.phase == UploadPhase.UPLOADED_NOT_ATTACHED -> {
                    FilledTonalButton(onClick = onRetry, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Outlined.Refresh, contentDescription = null)
                        Text(stringResource(R.string.common_retry), modifier = Modifier.padding(start = 8.dp))
                    }
                }
                job.phase != UploadPhase.COMPLETED && job.phase != UploadPhase.CANCELLED -> {
                    OutlinedButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Outlined.Cancel, contentDescription = null)
                        Text(stringResource(R.string.common_cancel), modifier = Modifier.padding(start = 8.dp))
                    }
                }
            }
        }
    }
}

@Composable
fun GlobalUploadBanner(state: UploadQueueUiState, onOpen: () -> Unit) {
    val current = state.current ?: return
    Card(
        onClick = onOpen,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
    ) {
        Row(
            modifier = Modifier.padding(10.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            UploadJobThumbnail(job = current, modifier = Modifier.size(38.dp))
            Column(Modifier.weight(1f)) {
                Text("Загрузка ${state.completed}/${state.total}", style = MaterialTheme.typography.labelLarge)
                Text(current.fileName, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                if (current.phase.isExecuting()) {
                    if (current.bytesTotal > 0) {
                        LinearProgressIndicator(
                            progress = { state.progress },
                            modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
                        )
                    } else {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth().padding(top = 4.dp))
                    }
                }
            }
        }
    }
}
