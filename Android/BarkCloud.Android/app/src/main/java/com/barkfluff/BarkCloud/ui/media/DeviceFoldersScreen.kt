package com.barkfluff.BarkCloud.ui.media

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import android.provider.MediaStore
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.outlined.CloudDone
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import coil3.compose.AsyncImage
import com.barkfluff.BarkCloud.BarkCloudApplication
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.BiometricGate
import com.barkfluff.BarkCloud.data.NotificationPermissionRequests
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import com.barkfluff.BarkCloud.data.gallery.DeviceDeleteMode
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.files.ui.formatChildCount
import com.barkfluff.BarkCloud.ui.components.CloudAlbumPickerDialog
import com.barkfluff.BarkCloud.ui.components.RefreshableContentSurface
import com.barkfluff.BarkCloud.ui.components.localPreviewModel
import com.barkfluff.BarkCloud.ui.components.loadAllAlbums
import com.barkfluff.BarkCloud.ui.gallery.DeviceMediaViewer
import com.barkfluff.BarkCloud.ui.selection.SelectionAction
import com.barkfluff.BarkCloud.ui.selection.SelectionActionBar
import com.barkfluff.BarkCloud.ui.selection.dragSelection
import com.barkfluff.BarkCloud.ui.selection.rememberDragSelection
import com.barkfluff.BarkCloud.ui.selection.toggled
import com.barkfluff.BarkCloud.ui.selection.viewportBounds
import com.barkfluff.BarkCloud.ui.selection.visibleCells
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class DeviceFolder(val key: String, val name: String, val media: List<DeviceMedia>)

internal fun deviceFolders(media: List<DeviceMedia>): List<DeviceFolder> =
    media.groupBy { it.folderKey }.map { (key, items) ->
        DeviceFolder(key, items.first().folderName, items.sortedByDescending { it.dateTakenMillis })
    }.sortedBy { it.name.lowercase() }

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun DeviceFoldersScreen(onNavigateUp: () -> Unit) {
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    val app = context.applicationContext as BarkCloudApplication
    val backup = remember { AutoUploadSettings(context) }
    val scope = rememberCoroutineScope()
    val viewModel: DeviceFoldersViewModel = viewModel(factory = DeviceFoldersViewModel.factory())
    val state by viewModel.state.collectAsStateWithLifecycle()
    var backupRevision by remember { mutableIntStateOf(0) }
    val syncEnabled: (String) -> Boolean = { backupRevision; backup.enabled && backup.includesFolder(it) }
    var selectedKey by rememberSaveable { mutableStateOf<String?>(null) }
    var viewer by remember { mutableStateOf<DeviceMedia?>(null) }
    var revision by remember { mutableIntStateOf(0) }
    var loading by remember { mutableStateOf(true) }
    var hasAccess by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf(false) }
    var dragging by remember { mutableStateOf(false) }
    var actionBarHeightPx by remember { mutableIntStateOf(0) }
    var pickAlbums by remember { mutableStateOf<List<AlbumCard>?>(null) }
    val snackbarHostState = remember { SnackbarHostState() }
    val gridState = rememberLazyGridState()
    val permissions = remember {
        if (Build.VERSION.SDK_INT >= 33) {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO) +
                if (Build.VERSION.SDK_INT >= 34) arrayOf(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) else emptyArray()
        } else arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { revision++ }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { revision++; backupRevision++ }

    // Идентификатор операции, ожидающей системного ответа: читается в момент результата.
    val awaitingOperationId by rememberUpdatedState(state.awaitingSystemOperationId)
    val deleteLauncher = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        awaitingOperationId?.let { viewModel.onSystemDeleteResult(it, result.resultCode == Activity.RESULT_OK) }
    }

    suspend fun reload() {
        fun granted(permission: String) = ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED
        val partial = Build.VERSION.SDK_INT >= 34 && granted(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
        val images = granted(if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_IMAGES else Manifest.permission.READ_EXTERNAL_STORAGE) || partial
        val videos = granted(if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_VIDEO else Manifest.permission.READ_EXTERNAL_STORAGE) || partial
        hasAccess = images || videos
        loading = true
        error = false
        try {
            if (hasAccess) viewModel.reload(images, videos)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            error = true
        } finally {
            loading = false
        }
    }
    LaunchedEffect(revision) { reload() }
    LaunchedEffect(state.snackbar) {
        val message = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(message)
        viewModel.snackbarShown()
    }

    val folders = state.folders
    val selected = folders.firstOrNull { it.key == selectedKey }
    val selecting = state.selected.isNotEmpty()
    val back = { if (selectedKey != null) selectedKey = null else onNavigateUp() }
    BackHandler(enabled = state.busy || state.staging != null || state.awaitingSystemOperationId != null) { /* операция выполняется или ждёт системы: навигация заблокирована */ }
    BackHandler(enabled = selecting && !state.busy) { viewModel.clearSelection() }
    BackHandler(enabled = selectedKey != null && !selecting && !state.busy) { selectedKey = null }

    val dragController = rememberDragSelection(
        orderedKeys = selected?.media?.map { it.mediaKey }.orEmpty(),
        selected = state.selected,
        onSelectionChange = viewModel::setSelection,
        visibleCells = { gridState.visibleCells() },
        viewport = { gridState.viewportBounds(bottomInsetPx = actionBarHeightPx.toFloat()) },
        scrollBy = { gridState.scrollBy(it) },
        onDragActiveChange = { dragging = it },
    )

    // Удаление локальных копий: системное подтверждение; отказ не трогает облако.
    // Системный запрос на удаление локальных копий: операция уже записана до показа диалога.
    LaunchedEffect(state.systemDelete) {
        val request = state.systemDelete ?: return@LaunchedEffect
        viewModel.systemDeleteRequested()
        try {
            val intent = MediaStore.createDeleteRequest(context.contentResolver, request.uris)
            deleteLauncher.launch(IntentSenderRequest.Builder(intent.intentSender).build())
        } catch (e: Exception) {
            viewModel.onSystemDeleteResult(request.operationId, approved = false)
            snackbarHostState.showSnackbar(e.message ?: context.getString(R.string.common_error_generic))
        }
    }
    // Разблокировка Vault для скрытых копий: результат применяется к отложенному действию.
    LaunchedEffect(state.unlockFor) {
        if (state.unlockFor == null) return@LaunchedEffect
        if (activity == null) {
            viewModel.onUnlockResult(false)
            return@LaunchedEffect
        }
        val granted = BiometricGate.authenticate(activity, context.getString(R.string.vault_unlock_button))
        viewModel.onUnlockResult(granted)
    }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { viewModel.onScreenStopped() }

    Box(Modifier.fillMaxSize()) {
    RefreshableContentSurface(
        onRefresh = { if (!state.busy && !dragging) reload() },
        modifier = Modifier.fillMaxSize(),
        header = {
            CenterAlignedTopAppBar(
                title = { Text(selected?.name ?: stringResource(R.string.collections_device)) },
                windowInsets = WindowInsets(0, 0, 0, 0),
                navigationIcon = {
                    IconButton(onClick = back, enabled = !state.busy) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.files_back))
                    }
                },
            )
        },
    ) {
        if (loading && folders.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        } else if (!hasAccess || error || folders.isEmpty()) {
            Column(Modifier.fillMaxSize().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                Text(stringResource(if (error) R.string.device_media_error else if (!hasAccess) R.string.device_media_permission else R.string.device_folders_empty))
                if (!hasAccess) Button(onClick = { permissionLauncher.launch(permissions) }) { Text(stringResource(R.string.device_media_allow)) }
            }
        } else if (selected != null) {
            Box(Modifier.fillMaxSize()) {
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(110.dp),
                    modifier = Modifier.fillMaxSize().dragSelection(dragController) { key ->
                        if (state.busy || state.staging != null) return@dragSelection
                        if (selecting) {
                            viewModel.setSelection(state.selected.toggled(key))
                        } else {
                            viewer = selected.media.firstOrNull { it.mediaKey == key }
                        }
                    },
                    state = gridState,
                    contentPadding = PaddingValues(12.dp),
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                    userScrollEnabled = !dragging,
                ) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        Row(
                            Modifier.fillMaxWidth().padding(bottom = 12.dp, top = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(16.dp),
                        ) {
                            Text(stringResource(R.string.device_folder_sync), modifier = Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                            val label = stringResource(R.string.device_folder_sync)
                            Switch(
                                checked = syncEnabled(selected.key),
                                onCheckedChange = { enabled ->
                                    backup.setFolderEnabled(selected.key, enabled, folders.map { it.key }.toSet())
                                    backupRevision++
                                    scope.launch {
                                        if (enabled) {
                                            NotificationPermissionRequests.request()
                                            app.uploadQueue.resumeBackup()
                                        }
                                        AutoUploadScheduler.apply(context, backup.policy)
                                    }
                                },
                                modifier = Modifier.semantics { contentDescription = label },
                            )
                        }
                    }
                    items(selected.media, key = { it.mediaKey }) { media ->
                        DeviceMediaCell(
                            media = media,
                            cloudStatus = state.cloudStates[media.mediaKey],
                            selecting = selecting,
                            selected = media.mediaKey in state.selected,
                            refreshToken = revision,
                            onAppear = { viewModel.requestPresence(media, force = false) },
                        )
                    }
                }
                if (state.staging != null || state.busy) {
                    val progress = state.staging
                    Box(
                        Modifier.fillMaxSize().background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.3f)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            LoadingIndicator()
                            if (progress != null) {
                                Text("${progress.done}/${progress.total}", color = Color.White, modifier = Modifier.padding(top = 12.dp))
                                TextButton(onClick = viewModel::cancelStaging) {
                                    Text(stringResource(R.string.device_staging_cancel), color = Color.White)
                                }
                            }
                        }
                    }
                }
                if (selecting && state.staging == null) {
                    val count = state.selected.size
                    SelectionActionBar(
                        modifier = Modifier.align(Alignment.BottomCenter).onSizeChanged { actionBarHeightPx = it.height },
                        actions = buildList {
                            add(SelectionAction(stringResource(R.string.device_upload_selected_count, count), enabled = !state.busy) {
                                viewModel.uploadSelected()
                            })
                            add(SelectionAction(stringResource(R.string.album_add_selected, count), enabled = !state.busy) {
                                scope.launch { pickAlbums = loadAllAlbums(app.albumRepository) }
                            })
                            add(SelectionAction(stringResource(R.string.device_keep_cloud_count, count), enabled = !state.busy) {
                                viewModel.prepareDelete(DeviceDeleteMode.KEEP_CLOUD)
                            })
                            add(SelectionAction(stringResource(R.string.device_delete_everywhere_count, count), enabled = !state.busy) {
                                viewModel.prepareDelete(DeviceDeleteMode.EVERYWHERE)
                            })
                            add(SelectionAction(stringResource(R.string.common_cancel), enabled = !state.busy) {
                                viewModel.clearSelection()
                            })
                        },
                    )
                }
            }
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(180.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(
                    state.operations,
                    key = { "operation-${it.id}" },
                    span = { GridItemSpan(maxLineSpan) },
                ) { operation ->
                    OperationBanner(
                        operation = operation,
                        enabled = !state.busy,
                        onResume = { viewModel.resumeOperation(operation.id) },
                        onDiscard = { viewModel.discardOperation(operation.id) },
                    )
                }
                items(folders, key = { it.key }) { folder ->
                    ElevatedCard(Modifier.fillMaxWidth().clickable { selectedKey = folder.key }) {
                        AsyncImage(folder.media.first().localPreviewModel(512), folder.name, Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(topStart = 12.dp, topEnd = 12.dp)), contentScale = ContentScale.Crop)
                        Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(folder.name, style = MaterialTheme.typography.titleMedium, maxLines = 1)
                                Text(formatChildCount(context, folder.media.size), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            // Значок — по фактическим облачным копиям фото папки, не по настройке автозагрузки.
                            if (folder.media.any { state.cloudStates[it.mediaKey] == MediaCloudStatus.IN_CLOUD }) Icon(
                                Icons.Outlined.CloudDone,
                                stringResource(R.string.device_folder_sync_enabled),
                                tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(20.dp),
                            )
                        }
                    }
                }
            }
        }
    }

    state.confirmation?.let { confirmation ->
        val plan = confirmation.plan
        AlertDialog(
            onDismissRequest = viewModel::cancelDelete,
            title = { Text(stringResource(R.string.gallery_delete_selected_title)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        if (confirmation.mode == DeviceDeleteMode.KEEP_CLOUD) {
                            stringResource(R.string.device_confirm_keep_cloud, plan.devicePhotos.size)
                        } else {
                            stringResource(R.string.device_confirm_everywhere, plan.devicePhotos.size, plan.cloudCopyCount)
                        },
                    )
                    if (plan.skipped.isNotEmpty()) Text(stringResource(R.string.device_confirm_skipped, plan.skipped.size))
                }
            },
            confirmButton = {
                TextButton(enabled = plan.devicePhotos.isNotEmpty(), onClick = viewModel::confirmDelete) {
                    Text(stringResource(R.string.common_delete), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = { TextButton(onClick = viewModel::cancelDelete) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
    pickAlbums?.let { choices ->
        CloudAlbumPickerDialog(
            albums = choices,
            enabled = !state.busy,
            onPick = { album ->
                pickAlbums = null
                viewModel.addSelectedToAlbum(album.id)
            },
            onDismiss = { pickAlbums = null },
        )
    }
    SnackbarHost(snackbarHostState, modifier = Modifier.align(Alignment.BottomCenter))
    }

    DeviceMediaViewer(
        items = selected?.media.orEmpty(),
        selected = viewer,
        onDismiss = { viewer = null },
    )
}

@Composable
private fun DeviceMediaCell(
    media: DeviceMedia,
    cloudStatus: MediaCloudStatus?,
    selecting: Boolean,
    selected: Boolean,
    refreshToken: Int,
    onAppear: () -> Unit,
) {
    LaunchedEffect(media.mediaKey, refreshToken) { onAppear() }
    Box(Modifier.aspectRatio(1f).clip(MaterialTheme.shapes.small)) {
        AsyncImage(media.localPreviewModel(512), media.name, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        if (media.isVideo) Icon(Icons.Filled.PlayCircle, stringResource(R.string.viewer_play_video), Modifier.align(Alignment.Center))
        if (cloudStatus == MediaCloudStatus.IN_CLOUD) {
            Icon(
                Icons.Outlined.CloudDone,
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.align(Alignment.TopStart).padding(4.dp).size(18.dp),
            )
        }
        if (selecting) {
            Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.primary.copy(alpha = if (selected) 0.25f else 0f)))
            Icon(
                if (selected) Icons.Filled.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                contentDescription = null,
                tint = if (selected) MaterialTheme.colorScheme.primary else Color.White,
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp),
            )
        }
    }
}

/**
 * Незавершённое удаление после перезапуска или блокировки. Подтверждённую операцию можно продолжить;
 * неподтверждённую системой — только отменить (облако при этом не трогается).
 */
@Composable
private fun OperationBanner(
    operation: OperationSummary,
    enabled: Boolean,
    onResume: () -> Unit,
    onDiscard: () -> Unit,
) {
    ElevatedCard(Modifier.fillMaxWidth().padding(bottom = 4.dp)) {
        Column(Modifier.fillMaxWidth().padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(stringResource(R.string.device_op_title, operation.photos), style = MaterialTheme.typography.titleSmall)
            if (operation.hidden) Text(stringResource(R.string.device_op_needs_unlock), color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                if (operation.phase == com.barkfluff.BarkCloud.data.gallery.DeleteOpPhase.APPROVED) {
                    TextButton(onClick = onResume, enabled = enabled) { Text(stringResource(R.string.device_op_resume)) }
                } else {
                    TextButton(onClick = onDiscard, enabled = enabled) { Text(stringResource(R.string.device_op_discard)) }
                }
            }
        }
    }
}
