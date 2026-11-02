package com.barkfluff.BarkCloud.ui.gallery

import android.app.Activity
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.formatSize
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import androidx.compose.foundation.layout.Box

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DeviceCleanupScreen(onNavigateUp: () -> Unit, viewModel: GalleryViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val deletion by viewModel.deletionState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    var checking by remember { mutableStateOf(false) }
    var departing by remember { mutableStateOf<List<DeviceMedia>>(emptyList()) }
    var pending by rememberSaveable(stateSaver = pendingPhotoDeleteSaver) { mutableStateOf<PendingPhotoDelete?>(null) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            val keys = pending?.deviceMediaKeys.orEmpty().toSet()
            departing = state.items.filter { it.mediaKey in keys }.take(12)
        }
        pending?.let { viewModel.completeSystemDelete(it, result.resultCode == Activity.RESULT_OK) }
        pending = null
    }
    LaunchedEffect(deletion.results.firstOrNull()?.id) {
        val result = deletion.results.firstOrNull() ?: return@LaunchedEffect
        viewModel.acknowledgeDeleteResult(result.id)
        snackbar.showSnackbar(context.getString(R.string.gallery_device_copies_deleted, result.deletedCount))
    }
    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(
                windowInsets = WindowInsets(0, 0, 0, 0),
                title = { Text(stringResource(R.string.gallery_free_device_space)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, stringResource(R.string.gallery_cleanup_back))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        DeviceCleanupContent(
            state = state,
            busy = checking || deletion.isBusy || departing.isNotEmpty(),
            departing = departing,
            onAnimationFinished = { departing = emptyList() },
            modifier = Modifier.padding(padding),
            onClean = {
                checking = true
                scope.launch {
                    try {
                        val candidates = viewModel.prepareDeviceCopyDeletion()
                        if (candidates.isEmpty()) {
                            snackbar.showSnackbar(context.getString(R.string.gallery_cleanup_none_confirmed))
                        } else {
                            val request = MediaStore.createDeleteRequest(context.contentResolver, candidates.map { it.uri })
                            val deletionRequest = PendingPhotoDelete(
                                candidates.map { it.mediaKey }, emptyList(), candidates.size, accountReclaim = true,
                            )
                            if (viewModel.beginSystemDelete(deletionRequest)) {
                                pending = deletionRequest
                                launcher.launch(IntentSenderRequest.Builder(request.intentSender).build())
                            }
                        }
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Exception) {
                        pending = null
                        viewModel.abortSystemDelete()
                        snackbar.showSnackbar(error.message ?: context.getString(R.string.common_error_generic))
                    } finally {
                        checking = false
                    }
                }
            },
        )
    }
}

@Composable
internal fun DeviceCleanupContent(
    state: GalleryUiState,
    busy: Boolean,
    modifier: Modifier = Modifier,
    departing: List<DeviceMedia> = emptyList(),
    onAnimationFinished: () -> Unit = {},
    onClean: () -> Unit,
) {
    val size = formatSize(LocalContext.current, state.reclaimableBytes)
    Column(
        modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            CleanupPreviewGrid(
                items = if (departing.isNotEmpty()) departing else state.items.filter {
                    state.cloudStates[it.mediaKey] == MediaCloudStatus.IN_CLOUD
                }.take(12),
                dissolving = departing.isNotEmpty(),
                onFinished = onAnimationFinished,
            )
        }
        if (busy) {
            CircularProgressIndicator()
            Text(stringResource(R.string.gallery_cleanup_checking))
        }
        Button(
            onClick = onClean,
            enabled = !busy && !state.isLoading && !state.isQueueing && state.reclaimableBytes > 0,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text(stringResource(R.string.gallery_cleanup_action, size))
        }
    }
}
