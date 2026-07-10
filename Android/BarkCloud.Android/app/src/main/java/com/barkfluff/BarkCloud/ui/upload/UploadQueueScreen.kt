package com.barkfluff.BarkCloud.ui.upload

import android.net.Uri
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Cancel
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UploadQueueScreen(
    onNavigateUp: () -> Unit,
    viewModel: UploadQueueViewModel = viewModel(factory = UploadQueueViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val visibleJobs = state.jobs.filter { it.createdAtMillis >= System.currentTimeMillis() - 60L * 60L * 1000L }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Загрузки") },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
    ) { padding ->
        LazyColumn(
            modifier = Modifier.fillMaxSize().padding(padding),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(items = visibleJobs, key = { job -> job.id }) { job ->
                UploadJobRow(job, onRetry = { viewModel.retry(job.id) }, onCancel = { viewModel.cancel(job.id) })
            }
        }
    }
}

@Composable
fun UploadJobRow(job: UploadJob, onRetry: () -> Unit, onCancel: () -> Unit) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
        androidx.compose.foundation.layout.Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MediaThumb(
                model = job.stagedFilePath?.let(::File) ?: job.sourceUri?.let(Uri::parse),
                isVideo = job.mimeType?.startsWith("video/") == true,
                contentDescription = job.fileName,
                modifier = Modifier.size(52.dp),
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(job.fileName, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(job.phase.label(), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (job.phase in setOf(UploadPhase.UPLOADING, UploadPhase.QUEUED, UploadPhase.UPLOADED, UploadPhase.ATTACHING)) {
                    LinearProgressIndicator(
                        progress = { if (job.bytesTotal > 0) (job.bytesSent.toFloat() / job.bytesTotal).coerceIn(0f, 1f) else 0f },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
                job.errorMessage?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
            }
            if (job.phase == UploadPhase.FAILED) {
                IconButton(onClick = onRetry) { Icon(Icons.Outlined.Refresh, contentDescription = "Повторить") }
            } else if (job.phase != UploadPhase.COMPLETED) {
                IconButton(onClick = onCancel) { Icon(Icons.Outlined.Cancel, contentDescription = "Отменить") }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GlobalUploadBanner(state: UploadQueueUiState, onOpen: () -> Unit) {
    val current = state.current ?: return
    Card(
        onClick = onOpen,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
    ) {
        androidx.compose.foundation.layout.Row(
            modifier = Modifier.padding(10.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MediaThumb(
                model = current.stagedFilePath?.let(::File) ?: current.sourceUri?.let(Uri::parse),
                isVideo = current.mimeType?.startsWith("video/") == true,
                contentDescription = current.fileName,
                modifier = Modifier.size(38.dp),
            )
            Column(Modifier.weight(1f)) {
                Text("Загрузка ${state.completed}/${state.total}", style = MaterialTheme.typography.labelLarge)
                Text(current.fileName, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                LinearProgressIndicator(progress = { state.progress }, modifier = Modifier.fillMaxWidth().padding(top = 4.dp))
            }
        }
    }
}

private fun UploadPhase.label(): String = when (this) {
    UploadPhase.QUEUED -> "В очереди"
    UploadPhase.UPLOADING -> "Отправляется"
    UploadPhase.UPLOADED, UploadPhase.ATTACHING -> "Размещается в облаке"
    UploadPhase.PAUSED -> "На паузе"
    UploadPhase.FAILED -> "Ошибка"
    UploadPhase.COMPLETED -> "Готово"
    UploadPhase.CANCELLED -> "Отменено"
}
