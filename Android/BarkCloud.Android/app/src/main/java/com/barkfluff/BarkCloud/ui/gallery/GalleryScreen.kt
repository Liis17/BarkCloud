package com.barkfluff.BarkCloud.ui.gallery

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.MediaStore
import androidx.activity.compose.BackHandler
import androidx.activity.result.IntentSenderRequest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CloudDone
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.PullToRefreshDefaults
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.RefreshContentSurface
import com.barkfluff.BarkCloud.ui.components.RefreshIndicator
import com.barkfluff.BarkCloud.ui.components.contentSurfaceTopPx
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.RemoteImage
import com.barkfluff.BarkCloud.ui.components.mediaDateSections
import com.barkfluff.BarkCloud.ui.components.rememberRemoteOpener
import com.barkfluff.BarkCloud.ui.media.MediaCell
import com.barkfluff.BarkCloud.ui.media.MediaGridViewModel
import com.barkfluff.BarkCloud.ui.theme.BarkMotion
import com.barkfluff.BarkCloud.ui.upload.UploadQueueUiState
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.min

private fun requiredMediaPermissions(): Array<String> =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
    } else {
        arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }

internal val pendingPhotoDeleteSaver = listSaver<PendingPhotoDelete?, Any>(
    save = { pending ->
        if (pending == null) emptyList() else listOf(
            ArrayList(pending.deviceMediaKeys),
            ArrayList(pending.cloudFileIds),
            pending.totalCount,
            pending.accountReclaim,
        )
    },
    restore = { saved ->
        if (saved.isEmpty()) null else PendingPhotoDelete(
            deviceMediaKeys = saved[0] as List<String>,
            cloudFileIds = saved[1] as List<String>,
            totalCount = saved[2] as Int,
            accountReclaim = saved[3] as Boolean,
        )
    },
)

private enum class AccountMenuDestination {
    PROFILE,
    SETTINGS,
    TRASH,
    UPLOADS,
}

@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun GalleryScreen(
    uploadState: UploadQueueUiState,
    onOpenProfile: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenTrash: () -> Unit,
    onOpenUploads: () -> Unit,
    viewModel: GalleryViewModel = viewModel(factory = GalleryViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val deletionState by viewModel.deletionState.collectAsStateWithLifecycle()
    val photos: MediaGridViewModel = viewModel(key = "cloud-photos", factory = MediaGridViewModel.factory(CloudMediaKind.PHOTO))
    val videos: MediaGridViewModel = viewModel(key = "cloud-videos", factory = MediaGridViewModel.factory(CloudMediaKind.VIDEO))
    val cloudPhotos by photos.state.collectAsStateWithLifecycle()
    val cloudVideos by videos.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()
    val permissions = remember { requiredMediaPermissions() }
    val snackbarHostState = remember { SnackbarHostState() }
    val gridState = rememberLazyGridState()
    val pullToRefreshState = rememberPullToRefreshState()
    var viewer by remember { mutableStateOf<DeviceMedia?>(null) }
    var cloudViewer by remember { mutableStateOf<MediaAsset?>(null) }
    var deleteDialogOpen by remember { mutableStateOf(false) }
    var pendingDelete by rememberSaveable(stateSaver = pendingPhotoDeleteSaver) {
        mutableStateOf<PendingPhotoDelete?>(null)
    }
    var accountDialogOpen by remember { mutableStateOf(false) }
    var pendingNavigation by remember { mutableStateOf<AccountMenuDestination?>(null) }
    var requestReclaimAfterDismiss by remember { mutableStateOf(false) }
    var profile by remember { mutableStateOf<User?>(null) }
    var storageUsed by remember { mutableStateOf<Long?>(null) }
    var storageLimit by remember { mutableStateOf<Long?>(null) }
    var storageLoading by remember { mutableStateOf(false) }
    var refreshing by remember { mutableStateOf(false) }
    var refreshQueued by remember { mutableStateOf(false) }
    var selectionToolbarHeightPx by remember { mutableStateOf(0) }

    val openRemote = rememberRemoteOpener()
    val timeline = remember(cloudPhotos.items, cloudVideos.items) {
        (cloudPhotos.items + cloudVideos.items).map { PhotoTimelineItem.Cloud(it) }
            .sortedByDescending { it.dateMillis }
    }
    val dateSections = remember(timeline) { mediaDateSections(timeline) { it.dateMillis } }
    val cloudAssets = remember(timeline) { timeline.mapNotNull { (it as? PhotoTimelineItem.Cloud)?.asset } }
    val selectedDeviceMediaKeys = state.selected
        .filter { it.startsWith(DEVICE_SELECTION_PREFIX) }
        .map { it.removePrefix(DEVICE_SELECTION_PREFIX) }
    val selectedCloudIds = state.selected
        .filter { it.startsWith(CLOUD_SELECTION_PREFIX) }
        .map { it.removePrefix(CLOUD_SELECTION_PREFIX) }

    fun requestRefresh() {
        if (deletionState.isBusy) return
        if (refreshing) {
            refreshQueued = true
            return
        }
        refreshing = true
        scope.launch {
            try {
                do {
                    refreshQueued = false
                    coroutineScope {
                        launch { delay(1_500) }
                        val local = async { if (state.permissionGranted) viewModel.reloadAndWait() }
                        val cloudPhotosReload = async { photos.reloadAndWait() }
                        val cloudVideosReload = async { videos.reloadAndWait() }
                        local.await()
                        cloudPhotosReload.await()
                        cloudVideosReload.await()
                    }
                } while (refreshQueued)
            } finally {
                refreshing = false
            }
        }
    }

    val statusBarHeight = androidx.compose.foundation.layout.WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val statusBarHeightPx = with(density) { statusBarHeight.toPx() }
    val headerHeight = statusBarHeight + 64.dp
    val headerHeightPx = with(density) { headerHeight.toPx() }
    val maxHeaderCollapsePx = headerHeightPx
    val pullThresholdPx = with(density) { PullToRefreshDefaults.PositionalThreshold.toPx() }
    var headerCollapsePx by rememberSaveable { mutableStateOf(0f) }
    val headerScrollConnection = remember(gridState, maxHeaderCollapsePx) {
        object : NestedScrollConnection {
            override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                val delta = available.y
                if (delta < 0f) {
                    val consumed = min(-delta, maxHeaderCollapsePx - headerCollapsePx)
                    if (consumed > 0f) {
                        headerCollapsePx += consumed
                        return Offset(0f, -consumed)
                    }
                } else if (delta > 0f) {
                    val consumed = min(delta, headerCollapsePx)
                    if (consumed > 0f) {
                        headerCollapsePx -= consumed
                        return Offset(0f, consumed)
                    }
                }
                return Offset.Zero
            }

            override fun onPostScroll(
                consumed: Offset,
                available: Offset,
                source: NestedScrollSource,
            ): Offset {
                if (available.y <= 0f || gridState.canScrollBackward) return Offset.Zero
                val expanded = min(available.y, headerCollapsePx)
                if (expanded <= 0f) return Offset.Zero
                headerCollapsePx -= expanded
                return Offset(0f, expanded)
            }
        }
    }

    LaunchedEffect(Unit) {
        runCatching { app.userRepository.getUser() }.onSuccess { profile = it }
    }
    LaunchedEffect(accountDialogOpen) {
        if (accountDialogOpen) {
            viewModel.refreshAutoUploadPolicy()
            storageLoading = true
            storageUsed = null
            storageLimit = null
            runCatching { app.fileTransfer.storageInfo() }.onSuccess {
                storageUsed = it.used
                storageLimit = it.limit
            }
            storageLoading = false
        }
    }
    LaunchedEffect(accountDialogOpen, pendingNavigation) {
        if (!accountDialogOpen && pendingNavigation != null) {
            val destination = pendingNavigation
            pendingNavigation = null
            when (destination) {
                AccountMenuDestination.PROFILE -> onOpenProfile()
                AccountMenuDestination.SETTINGS -> onOpenSettings()
                AccountMenuDestination.TRASH -> onOpenTrash()
                AccountMenuDestination.UPLOADS -> onOpenUploads()
                null -> Unit
            }
        }
    }
    LaunchedEffect(Unit) { photos.loadIfNeeded(); videos.loadIfNeeded() }
    LaunchedEffect(cloudPhotos.snackbar) {
        cloudPhotos.snackbar?.let { snackbarHostState.showSnackbar(it); photos.snackbarShown() }
    }
    LaunchedEffect(cloudVideos.snackbar) {
        cloudVideos.snackbar?.let { snackbarHostState.showSnackbar(it); videos.snackbarShown() }
    }
    LaunchedEffect(state.snackbar) {
        state.snackbar?.let { snackbarHostState.showSnackbar(it); viewModel.snackbarShown() }
    }
    LaunchedEffect(deletionState.deletedCloudIds) {
        val deletedIds = deletionState.deletedCloudIds
        if (deletedIds.isNotEmpty()) {
            photos.removeItems(deletedIds)
            videos.removeItems(deletedIds)
            viewModel.acknowledgeDeletedCloudIds(deletedIds)
        }
    }
    LaunchedEffect(deletionState.results.firstOrNull()?.id) {
        val result = deletionState.results.firstOrNull() ?: return@LaunchedEffect
        val message = if (result.accountReclaim) {
            context.getString(R.string.gallery_device_copies_deleted, result.deletedCount)
        } else {
            context.getString(R.string.gallery_delete_result, result.deletedCount, result.totalCount)
        }
        snackbarHostState.showSnackbar(message)
        viewModel.acknowledgeDeleteResult(result.id)
    }
    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { result -> viewModel.onPermissionResult(result.values.all { it }) }
    val deleteLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartIntentSenderForResult(),
    ) { result ->
        val pending = pendingDelete
        pendingDelete = null
        if (pending != null) {
            viewModel.completeSystemDelete(pending, result.resultCode == Activity.RESULT_OK)
        }
    }

    fun launchSystemDelete(
        pending: PendingPhotoDelete,
        uris: List<android.net.Uri>,
    ) {
        if (uris.isEmpty() || !viewModel.beginSystemDelete(pending)) return
        try {
            val request = MediaStore.createDeleteRequest(context.contentResolver, uris)
            pendingDelete = pending
            deleteLauncher.launch(IntentSenderRequest.Builder(request.intentSender).build())
        } catch (e: Exception) {
            pendingDelete = null
            viewModel.abortSystemDelete()
            scope.launch { snackbarHostState.showSnackbar(e.message ?: context.getString(R.string.common_error_generic)) }
        }
    }

    fun beginDeviceDelete(deviceMediaKeys: List<String>, cloudIds: List<String>) {
        val targets = viewModel.selectedDeviceTargets(deviceMediaKeys)
        if (targets.isEmpty()) return
        val pending = PendingPhotoDelete(
            deviceMediaKeys = targets.map { it.mediaKey },
            cloudFileIds = cloudIds,
            totalCount = targets.size + cloudIds.size,
        )
        launchSystemDelete(pending, targets.map { it.uri })
    }

    fun deleteCloudOnly(fileIds: List<String>) {
        viewModel.deleteCloudOnly(fileIds)
    }

    LaunchedEffect(accountDialogOpen, requestReclaimAfterDismiss) {
        if (!accountDialogOpen && requestReclaimAfterDismiss) {
            requestReclaimAfterDismiss = false
            viewModel.prepareDeviceCopyDeletion { uris, mediaKeys ->
                launchSystemDelete(
                    pending = PendingPhotoDelete(
                        deviceMediaKeys = mediaKeys,
                        cloudFileIds = emptyList(),
                        totalCount = mediaKeys.size,
                        accountReclaim = true,
                    ),
                    uris = uris,
                )
            }
        }
    }

    LaunchedEffect(Unit) {
        val permissionGranted = permissions.all {
            ContextCompat.checkSelfPermission(context, it) == PackageManager.PERMISSION_GRANTED
        }
        if (permissionGranted) viewModel.onPermissionResult(true) else permissionLauncher.launch(permissions)
    }
    BackHandler(enabled = state.selecting) { viewModel.clearSelection() }

    val profileName = profile?.let { listOf(it.firstName, it.lastName).filter(String::isNotBlank).joinToString(" ") }
        ?.takeIf { it.isNotBlank() }
        ?: profile?.username
    val avatarUrl = profile?.let { it.profilePicture.ifEmpty { it.profilePicturePreview }.ifEmpty { null } }
    Box(
        Modifier.fillMaxSize()
            .background(MaterialTheme.colorScheme.background),
    ) {
        GalleryHeader(
            height = headerHeight,
            avatarUrl = avatarUrl,
            onAccount = { accountDialogOpen = true },
            selecting = state.selecting,
            selectedCount = state.selected.size,
            onClearSelection = viewModel::clearSelection,
            enabled = !deletionState.isBusy,
            modifier = Modifier.align(Alignment.TopCenter).graphicsLayer {
                translationY = if (state.selecting) 0f else -headerCollapsePx
            },
        )

        PullToRefreshBox(
            state = pullToRefreshState,
            isRefreshing = refreshing,
            onRefresh = ::requestRefresh,
            modifier = Modifier.fillMaxSize().nestedScroll(headerScrollConnection),
            indicator = {
                RefreshIndicator(
                    pullProgress = { pullToRefreshState.distanceFraction },
                    isRefreshing = refreshing,
                    modifier = Modifier.align(Alignment.TopCenter).padding(top = statusBarHeight),
                    gapHeightPx = {
                        headerHeightPx - statusBarHeightPx - (if (state.selecting) 0f else headerCollapsePx) +
                            pullThresholdPx * pullToRefreshState.distanceFraction
                    },
                )
            },
        ) {
            RefreshContentSurface(
                modifier = Modifier.fillMaxSize(),
                topOffsetPx = {
                    contentSurfaceTopPx(
                        headerHeightPx = headerHeightPx,
                        collapsedHeaderPx = if (state.selecting) 0f else headerCollapsePx,
                    )
                },
                pullOffsetPx = { pullThresholdPx * pullToRefreshState.distanceFraction },
            ) {
                LazyVerticalGrid(
                    columns = GridCells.Adaptive(minSize = 120.dp),
                    modifier = Modifier.fillMaxSize(),
                    state = gridState,
                    contentPadding = PaddingValues(
                        bottom = 20.dp + if (state.selecting) with(density) { selectionToolbarHeightPx.toDp() } else 0.dp,
                    ),
                ) {
                    if (!refreshing && timeline.isEmpty() && (cloudPhotos.isLoading || cloudVideos.isLoading)) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                                LoadingIndicator()
                            }
                        }
                    }
                    dateSections.forEach { section ->
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Text(
                                section.title,
                                style = MaterialTheme.typography.titleSmall,
                                modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp),
                            )
                        }
                        items(section.items, key = { it.key }) { item ->
                            when (item) {
                                is PhotoTimelineItem.Device -> GalleryCell(
                                    media = item.media,
                                    selecting = state.selecting,
                                    selected = item.key in state.selected,
                                    cloudStatus = state.cloudStates[item.media.mediaKey],
                                    onAppear = { viewModel.observeCloudPresence(item.media) },
                                    onTap = {
                                        if (!deletionState.isBusy) {
                                            if (state.selecting) viewModel.toggle(item.key) else viewer = item.media
                                        }
                                    },
                                    onLongPress = { if (!deletionState.isBusy) viewModel.startSelecting(item.key) },
                                )
                                is PhotoTimelineItem.Cloud -> MediaCell(
                                    asset = item.asset,
                                    onTap = {
                                        if (!deletionState.isBusy) {
                                            if (state.selecting) viewModel.toggle(item.key) else cloudViewer = item.asset
                                        }
                                    },
                                    onFavorite = {},
                                    onAddToVault = {},
                                    onLongPress = { if (!deletionState.isBusy) viewModel.startSelecting(item.key) },
                                    selecting = state.selecting,
                                    selected = item.key in state.selected,
                                )
                            }
                        }
                    }
                    if (timeline.isEmpty() && !cloudPhotos.isLoading && !cloudVideos.isLoading) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Text(stringResource(R.string.photos_empty), modifier = Modifier.padding(24.dp))
                        }
                    }
                    if (cloudPhotos.canLoadMore || cloudVideos.canLoadMore) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            TextButton(
                                onClick = { photos.loadMore(); videos.loadMore() },
                                enabled = !deletionState.isBusy && !cloudPhotos.isLoadingMore && !cloudVideos.isLoadingMore,
                                modifier = Modifier.fillMaxWidth(),
                            ) { Text(stringResource(R.string.common_load_more)) }
                        }
                    }
                }
            }
        }

        if (state.selecting) {
            GallerySelectionToolbar(
                modifier = Modifier.align(Alignment.BottomCenter).onSizeChanged { selectionToolbarHeightPx = it.height },
                actions = state.selectionActions,
                isBusy = deletionState.isBusy || state.isQueueing,
                isQueueing = state.isQueueing,
                onUpload = viewModel::uploadSelected,
                onFavorite = {
                    scope.launch {
                        val added = viewModel.addFavorites(selectedCloudIds)
                        viewModel.removeSelection(added.map { "$CLOUD_SELECTION_PREFIX$it" }.toSet())
                        snackbarHostState.showSnackbar(
                            context.getString(R.string.gallery_favorite_result, added.size, selectedCloudIds.size),
                        )
                    }
                },
                onDelete = { deleteDialogOpen = true },
            )
        }

        if (state.isQueueing) {
            Box(
                Modifier.fillMaxSize().background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.35f)),
                contentAlignment = Alignment.Center,
            ) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    LoadingIndicator()
                    Text(
                        text = stringResource(R.string.share_staging),
                        color = Color.White,
                        modifier = Modifier.padding(top = 12.dp),
                    )
                }
            }
        }

        SnackbarHost(snackbarHostState, modifier = Modifier.align(Alignment.BottomCenter))
    }

    if (deleteDialogOpen) {
        AlertDialog(
            onDismissRequest = { deleteDialogOpen = false },
            title = { Text(stringResource(R.string.gallery_delete_selected_title)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    photoDeleteActions(state.selectionActions).forEach { action ->
                        TextButton(
                            modifier = Modifier.fillMaxWidth(),
                            enabled = !deletionState.isBusy,
                            onClick = {
                                deleteDialogOpen = false
                                when (action) {
                                    PhotoDeleteAction.DEVICE -> beginDeviceDelete(
                                        selectedDeviceMediaKeys,
                                        emptyList(),
                                    )
                                    PhotoDeleteAction.CLOUD -> deleteCloudOnly(selectedCloudIds)
                                    PhotoDeleteAction.BOTH -> beginDeviceDelete(
                                        selectedDeviceMediaKeys,
                                        selectedCloudIds,
                                    )
                                }
                            },
                        ) {
                            Text(
                                when (action) {
                                    PhotoDeleteAction.DEVICE -> stringResource(R.string.gallery_delete_device_count, state.selectionActions.deviceCount)
                                    PhotoDeleteAction.CLOUD -> stringResource(R.string.gallery_delete_cloud_count, state.selectionActions.cloudCount)
                                    PhotoDeleteAction.BOTH -> stringResource(R.string.gallery_delete_both_count, state.selectionActions.deleteCount)
                                },
                            )
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { deleteDialogOpen = false }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }

    if (accountDialogOpen) {
        GalleryAccountDialog(
            displayName = profileName,
            username = profile?.username,
            avatarUrl = avatarUrl,
            storageUsed = storageUsed,
            storageLimit = storageLimit,
            storageLoading = storageLoading,
            autoUploadPolicy = state.autoUploadPolicy,
            uploadState = uploadState,
            reclaimableCount = state.reclaimableCount,
            onAutoUploadPolicyChange = viewModel::setAutoUploadPolicy,
            onOpenUploads = { accountDialogOpen = false; pendingNavigation = AccountMenuDestination.UPLOADS },
            onFreeDeviceSpace = { accountDialogOpen = false; requestReclaimAfterDismiss = true },
            onOpenProfile = { accountDialogOpen = false; pendingNavigation = AccountMenuDestination.PROFILE },
            onOpenSettings = { accountDialogOpen = false; pendingNavigation = AccountMenuDestination.SETTINGS },
            onOpenTrash = { accountDialogOpen = false; pendingNavigation = AccountMenuDestination.TRASH },
            onDismiss = { accountDialogOpen = false },
        )
    }

    cloudViewer?.let { asset ->
        CloudMediaViewer(
            assets = cloudAssets,
            initialIndex = cloudAssets.indexOf(asset).coerceAtLeast(0),
            onDismiss = { cloudViewer = null },
            onPlayVideo = { item -> openRemote(item.id, item.fileName) { message ->
                android.widget.Toast.makeText(context, message, android.widget.Toast.LENGTH_LONG).show()
            } },
        )
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

@Composable
internal fun GalleryHeader(
    height: androidx.compose.ui.unit.Dp,
    avatarUrl: String?,
    onAccount: () -> Unit,
    selecting: Boolean = false,
    selectedCount: Int = 0,
    onClearSelection: () -> Unit = {},
    enabled: Boolean = true,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier.fillMaxWidth().height(height).background(MaterialTheme.colorScheme.background),
    ) {
        Row(
            modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(64.dp).padding(horizontal = 16.dp),
            horizontalArrangement = Arrangement.End,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (selecting) {
                Text(
                    stringResource(R.string.gallery_selection_count, selectedCount),
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.weight(1f),
                )
                IconButton(onClick = onClearSelection, enabled = enabled) {
                    Icon(Icons.Outlined.Close, contentDescription = stringResource(R.string.common_cancel))
                }
            } else Box(
                Modifier.size(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.surfaceVariant)
                    .combinedClickable(onClick = onAccount),
                contentAlignment = Alignment.Center,
            ) {
                if (avatarUrl != null) {
                    RemoteImage(
                        url = avatarUrl,
                        contentDescription = stringResource(R.string.gallery_account_menu),
                        normalize = true,
                        modifier = Modifier.fillMaxSize().clip(CircleShape),
                    )
                } else {
                    Icon(Icons.Outlined.Person, contentDescription = stringResource(R.string.gallery_account_menu))
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun GallerySelectionToolbar(
    modifier: Modifier = Modifier,
    actions: PhotoSelectionActions,
    isBusy: Boolean,
    isQueueing: Boolean,
    onUpload: () -> Unit,
    onFavorite: () -> Unit,
    onDelete: () -> Unit,
) {
    Box(
        modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Surface(
            shape = MaterialTheme.shapes.extraLarge,
            color = MaterialTheme.colorScheme.surfaceContainerHigh,
            tonalElevation = 6.dp,
        ) {
            Column(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
                FlowRow(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(0.dp),
                ) {
                    if (actions.uploadCount > 0) {
                        TextButton(
                            onClick = onUpload,
                            enabled = !isBusy && !isQueueing,
                            contentPadding = PaddingValues(horizontal = 4.dp, vertical = 8.dp),
                        ) {
                            Text(stringResource(R.string.gallery_upload_selected, actions.uploadCount))
                        }
                    }
                    if (actions.favoriteCount > 0) {
                        TextButton(
                            onClick = onFavorite,
                            enabled = !isBusy,
                            contentPadding = PaddingValues(horizontal = 4.dp, vertical = 8.dp),
                        ) {
                            Text(stringResource(R.string.gallery_favorite_selected, actions.favoriteCount))
                        }
                    }
                    if (actions.deleteCount > 0) {
                        TextButton(
                            onClick = onDelete,
                            enabled = !isBusy,
                            contentPadding = PaddingValues(horizontal = 4.dp, vertical = 8.dp),
                        ) {
                            Text(stringResource(R.string.gallery_delete_selected, actions.deleteCount))
                        }
                    }
                }
            }
        }
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
    val badgeScale by androidx.compose.animation.core.animateFloatAsState(
        targetValue = if (selected) 1f else 0f,
        animationSpec = BarkMotion.spatial(),
        label = "selectBadgeScale",
    )
    val overlayAlpha by androidx.compose.animation.core.animateFloatAsState(
        targetValue = if (selected) 0.25f else 0f,
        animationSpec = BarkMotion.effect(),
        label = "selectOverlayAlpha",
    )
    Box(
        modifier = Modifier.aspectRatio(1f).padding(1.dp).clip(MaterialTheme.shapes.small)
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
            if (!selected) {
                Icon(
                    Icons.Outlined.RadioButtonUnchecked,
                    contentDescription = null,
                    tint = Color.White,
                    modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp),
                )
            }
            Icon(
                Icons.Filled.CheckCircle,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp).graphicsLayer {
                    scaleX = badgeScale
                    scaleY = badgeScale
                },
            )
            Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.primary.copy(alpha = overlayAlpha)))
        }
    }
}
