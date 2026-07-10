package com.barkfluff.BarkCloud.ui.gallery

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.MediaStore
import androidx.activity.result.IntentSenderRequest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CloudDone
import androidx.compose.material.icons.outlined.Checklist
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.Button
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.mediaDateSections

private fun requiredMediaPermissions(): Array<String> =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
    } else {
        arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GalleryScreen(
    viewModel: GalleryViewModel = viewModel(factory = GalleryViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current
    val permissions = remember { requiredMediaPermissions() }
    var viewer by remember { mutableStateOf<DeviceMedia?>(null) }
    var pendingDeleteKeys by remember { mutableStateOf<List<String>>(emptyList()) }
    val dateSections = remember(state.items) { mediaDateSections(state.items) { it.dateTakenMillis } }

    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { result -> viewModel.onPermissionResult(result.values.any { it }) }
    val deleteLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartIntentSenderForResult(),
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK && pendingDeleteKeys.isNotEmpty()) {
            viewModel.onDeviceCopiesDeleted(pendingDeleteKeys)
        }
        pendingDeleteKeys = emptyList()
    }

    LaunchedEffect(Unit) {
        val granted = permissions.all {
            ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
        }
        if (granted) viewModel.onPermissionResult(true) else launcher.launch(permissions)
    }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text(stringResource(R.string.tab_gallery)) },
                actions = {
                    Switch(
                        checked = state.autoUploadPolicy != com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy.OFF,
                        onCheckedChange = {
                            viewModel.setAutoUploadPolicy(
                                if (it) com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy.WIFI_ONLY
                                else com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy.OFF,
                            )
                        },
                    )
                    if (state.items.isNotEmpty()) {
                        IconButton(onClick = viewModel::toggleSelecting) {
                            Icon(
                                if (state.selecting) Icons.Outlined.Close else Icons.Outlined.Checklist,
                                contentDescription = null,
                            )
                        }
                    }
                },
            )
        },
        bottomBar = {
            if (state.selecting && state.selected.isNotEmpty()) {
                Button(
                    onClick = viewModel::uploadSelected,
                    enabled = !state.isQueueing,
                    modifier = Modifier.fillMaxWidth().padding(16.dp),
                ) {
                    Text(stringResource(R.string.gallery_upload_selected, state.selected.size))
                }
            } else if (state.reclaimableCount > 0) {
                OutlinedButton(
                    onClick = {
                        viewModel.prepareDeviceCopyDeletion { uris, mediaKeys ->
                            pendingDeleteKeys = mediaKeys
                            val request = MediaStore.createDeleteRequest(context.contentResolver, uris)
                            deleteLauncher.launch(IntentSenderRequest.Builder(request.intentSender).build())
                        }
                    },
                    modifier = Modifier.fillMaxWidth().padding(16.dp),
                ) {
                    Text(stringResource(R.string.gallery_free_device_space, state.reclaimableCount))
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            if (!state.permissionGranted) {
                PermissionRationale(onGrant = { launcher.launch(permissions) })
            } else {
                LazyVerticalGrid(
                    columns = GridCells.Fixed(3),
                    modifier = Modifier.fillMaxSize(),
                ) {
                    dateSections.forEach { section ->
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Text(
                                section.title,
                                style = MaterialTheme.typography.titleSmall,
                                modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp),
                            )
                        }
                        items(section.items, key = { it.id }) { media ->
                            GalleryCell(
                                media = media,
                                selecting = state.selecting,
                                selected = state.selected.contains(media.mediaKey),
                                cloudStatus = state.cloudStates[media.mediaKey],
                                onAppear = { viewModel.observeCloudPresence(media) },
                                onTap = {
                                    if (state.selecting) viewModel.toggle(media.mediaKey) else viewer = media
                                },
                                onLongPress = { viewModel.startSelecting(media.mediaKey) },
                            )
                        }
                    }
                }
            }

            if (state.isQueueing) {
                Box(
                    Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.35f)),
                    contentAlignment = Alignment.Center,
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator()
                        Text(text = stringResource(R.string.share_staging), color = Color.White, modifier = Modifier.padding(top = 12.dp))
                    }
                }
            }
        }
    }

    viewer?.let { media ->
        DeviceMediaViewer(
            media = media,
            onDismiss = { viewer = null },
            onPlayVideo = {
                val intent = Intent(Intent.ACTION_VIEW)
                    .setDataAndType(media.uri, "video/*")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                runCatching { context.startActivity(intent) }
            },
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GalleryCell(
    media: DeviceMedia,
    selecting: Boolean,
    selected: Boolean,
    cloudStatus: MediaCloudStatus?,
    onAppear: () -> Unit,
    onTap: () -> Unit,
    onLongPress: () -> Unit,
) {
    LaunchedEffect(media.id) { onAppear() }
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .padding(1.dp)
            .clip(MaterialTheme.shapes.small)
            .combinedClickable(onClick = onTap, onLongClick = onLongPress),
    ) {
        MediaThumb(
            model = media.uri,
            isVideo = media.isVideo,
            contentDescription = media.name,
            modifier = Modifier.fillMaxSize(),
        )
        if (cloudStatus == MediaCloudStatus.IN_CLOUD) {
            Icon(
                Icons.Filled.CloudDone,
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.align(Alignment.TopStart).padding(4.dp).size(18.dp),
            )
        }
        if (cloudStatus in setOf(MediaCloudStatus.CHECKING, MediaCloudStatus.QUEUED, MediaCloudStatus.UPLOADING)) {
            CircularProgressIndicator(
                modifier = Modifier.align(Alignment.TopStart).padding(5.dp).size(16.dp),
                color = Color.White,
                strokeWidth = 2.dp,
            )
        }
        if (selecting) {
            Icon(
                if (selected) Icons.Filled.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                contentDescription = null,
                tint = if (selected) MaterialTheme.colorScheme.primary else Color.White,
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp),
            )
            if (selected) {
                Box(
                    Modifier.fillMaxSize().background(MaterialTheme.colorScheme.primary.copy(alpha = 0.25f)),
                )
            }
        }
    }
}

@Composable
private fun PermissionRationale(onGrant: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            text = stringResource(R.string.gallery_permission_message),
            style = MaterialTheme.typography.bodyLarge,
        )
        Button(onClick = onGrant, modifier = Modifier.padding(top = 16.dp)) {
            Text(stringResource(R.string.gallery_permission_grant))
        }
    }
}
