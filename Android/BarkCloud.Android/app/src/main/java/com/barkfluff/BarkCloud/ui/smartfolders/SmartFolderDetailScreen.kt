package com.barkfluff.BarkCloud.ui.smartfolders

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.TextButton
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
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
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.ui.components.CloudAlbumPickerDialog
import com.barkfluff.BarkCloud.ui.components.CloudMediaTransitionLayout
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.ViewerPagination
import com.barkfluff.BarkCloud.ui.components.LocalCloudMediaViewerAssetId
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.RefreshableContentSurface
import com.barkfluff.BarkCloud.ui.components.cloudMediaSharedElement
import com.barkfluff.BarkCloud.ui.components.loadAllAlbums
import com.barkfluff.BarkCloud.ui.selection.SelectionAction
import com.barkfluff.BarkCloud.ui.selection.SelectionActionBar
import com.barkfluff.BarkCloud.ui.selection.dragSelection
import com.barkfluff.BarkCloud.ui.selection.rememberDragSelection
import com.barkfluff.BarkCloud.ui.selection.toggled
import com.barkfluff.BarkCloud.ui.selection.viewportBounds
import com.barkfluff.BarkCloud.ui.selection.visibleCells
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun SmartFolderDetailScreen(
    folderId: String,
    title: String,
    onNavigateUp: () -> Unit,
    viewModel: SmartFolderDetailViewModel = viewModel(factory = SmartFolderDetailViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val gridState = rememberLazyGridState()
    var viewer by remember { mutableStateOf<MediaAsset?>(null) }
    val app = LocalContext.current.applicationContext as BarkCloudApplication
    val scope = rememberCoroutineScope()
    var pickAlbums by remember { mutableStateOf<List<AlbumCard>?>(null) }
    var showDeleteSelected by remember { mutableStateOf(false) }
    var dragging by remember { mutableStateOf(false) }
    var actionBarHeightPx by remember { mutableIntStateOf(0) }
    val selecting = state.selected.isNotEmpty()
    BackHandler(enabled = selecting || state.busy) { if (!state.busy) viewModel.clearSelection() }
    val dragController = rememberDragSelection(
        orderedKeys = state.items.map { it.id },
        selected = state.selected,
        onSelectionChange = viewModel::setSelection,
        visibleCells = { gridState.visibleCells() },
        viewport = { gridState.viewportBounds(bottomInsetPx = actionBarHeightPx.toFloat()) },
        scrollBy = { gridState.scrollBy(it) },
        onDragActiveChange = { dragging = it },
    )

    LaunchedEffect(folderId) { viewModel.start(folderId) }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    val shouldLoadMore by remember {
        derivedStateOf {
            val last = gridState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= state.items.size - 6
        }
    }
    LaunchedEffect(shouldLoadMore) {
        if (shouldLoadMore) viewModel.loadMore()
    }

    CloudMediaTransitionLayout(viewer?.id) {
        Scaffold(
            contentWindowInsets = WindowInsets(0, 0, 0, 0),
            snackbarHost = { SnackbarHost(snackbarHostState) },
        ) { padding ->
            Box(Modifier.fillMaxSize().padding(padding)) {
                RefreshableContentSurface(
                    onRefresh = viewModel::reloadAndWait,
                    header = {
                        TopAppBar(
                            title = { Text(title, maxLines = 1) },
                            windowInsets = WindowInsets(0, 0, 0, 0),
                            navigationIcon = {
                                IconButton(onClick = onNavigateUp) {
                                    Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                                }
                            },
                        )
                    },
                    modifier = Modifier.fillMaxSize(),
                ) {
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(minSize = 120.dp),
                        state = gridState,
                        modifier = Modifier.fillMaxSize().dragSelection(dragController) { id ->
                            if (state.busy) return@dragSelection
                            if (selecting) {
                                viewModel.setSelection(state.selected.toggled(id))
                            } else {
                                viewer = state.items.firstOrNull { it.id == id }
                            }
                        },
                        userScrollEnabled = !dragging,
                    ) {
                        items(state.items, key = { it.id }) { asset ->
                            SmartFolderMediaCell(asset = asset, selecting = selecting, selected = asset.id in state.selected)
                        }
                    }
                }
                if (state.isEmpty) {
                    Text(
                        text = stringResource(R.string.smart_folder_empty),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.align(Alignment.Center),
                    )
                }
                if (state.isLoading || state.isLoadingMore) {
                    LoadingIndicator(Modifier.align(Alignment.Center))
                }
                if (selecting) {
                    val count = state.selected.size
                    SelectionActionBar(
                        modifier = Modifier.align(Alignment.BottomCenter).onSizeChanged { actionBarHeightPx = it.height },
                        actions = buildList {
                            add(SelectionAction(stringResource(R.string.album_add_selected, count), enabled = !state.busy) {
                                scope.launch { pickAlbums = loadAllAlbums(app.albumRepository) }
                            })
                            add(SelectionAction(stringResource(R.string.gallery_delete_selected, count), enabled = !state.busy) {
                                showDeleteSelected = true
                            })
                            add(SelectionAction(stringResource(R.string.common_cancel), enabled = !state.busy) {
                                viewModel.clearSelection()
                            })
                        },
                    )
                }
            }
        }
        CloudMediaViewer(
            assets = state.items,
            selectedAsset = viewer,
            onDismiss = { viewer = null },
            onDelete = { item -> viewModel.deleteFile(item.id) },
            pagination = ViewerPagination(hasMore = state.canLoadMore, isLoading = state.isLoading || state.isLoadingMore, pagesCompleted = state.pagesCompleted),
            onPageNearEnd = { viewModel.loadMore() },
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
    if (showDeleteSelected) {
        val count = state.selected.size
        AlertDialog(
            onDismissRequest = { showDeleteSelected = false },
            title = { Text(stringResource(R.string.gallery_delete_selected_title)) },
            text = { Text(stringResource(R.string.album_delete_selected_message, count)) },
            confirmButton = {
                TextButton(onClick = { showDeleteSelected = false; viewModel.deleteSelected() }) {
                    Text(stringResource(R.string.common_delete), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteSelected = false }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }
}

@Composable
private fun SmartFolderMediaCell(
    asset: MediaAsset,
    selecting: Boolean,
    selected: Boolean,
) {
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .padding(1.dp)
            .clip(MaterialTheme.shapes.small),
    ) {
        MediaThumb(
            model = asset.previewModel(512),
            isVideo = asset.isVideo,
            contentDescription = asset.fileName,
            modifier = Modifier.fillMaxSize().cloudMediaSharedElement(
                key = asset.id,
                visible = LocalCloudMediaViewerAssetId.current != asset.id,
            ),
        )
        if (selecting) {
            Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.primary.copy(alpha = if (selected) 0.25f else 0f)))
            Icon(
                if (selected) Icons.Filled.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                contentDescription = null,
                tint = if (selected) MaterialTheme.colorScheme.primary else Color.White,
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp),
            )
        }
    }
}
