package com.barkfluff.BarkCloud.ui.vault

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.ui.components.CloudAlbumPickerDialog
import com.barkfluff.BarkCloud.ui.components.CloudMediaTransitionLayout
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.LocalCloudMediaViewerAssetId
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.cloudMediaSharedElement
import com.barkfluff.BarkCloud.ui.components.loadAllAlbums
import com.barkfluff.BarkCloud.ui.selection.SelectionAction
import com.barkfluff.BarkCloud.ui.selection.SelectionActionBar
import com.barkfluff.BarkCloud.ui.selection.dragSelection
import com.barkfluff.BarkCloud.ui.selection.rememberDragSelection
import com.barkfluff.BarkCloud.ui.selection.toggled
import com.barkfluff.BarkCloud.ui.selection.viewportBounds
import com.barkfluff.BarkCloud.ui.selection.visibleCells
import com.barkfluff.BarkCloud.data.vault.VaultItem
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun VaultScreen(
    onNavigateUp: () -> Unit,
    viewModel: VaultViewModel = viewModel(factory = VaultViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    val app = context.applicationContext as BarkCloudApplication
    val scope = rememberCoroutineScope()
    val unlockTitle = stringResource(R.string.vault_unlock_button)
    var viewer by remember { mutableStateOf<MediaAsset?>(null) }
    val gridState = rememberLazyGridState()
    val snackbarHostState = remember { SnackbarHostState() }
    var pickAlbums by remember { mutableStateOf<List<AlbumCard>?>(null) }
    var showDeleteSelected by remember { mutableStateOf(false) }
    var dragging by remember { mutableStateOf(false) }
    var actionBarHeightPx by remember { mutableIntStateOf(0) }
    val vaultAssets = remember(state.items) { state.items.map { it.toMediaAsset() } }
    val selecting = state.selected.isNotEmpty()
    BackHandler(enabled = selecting || state.busy) { if (!state.busy) viewModel.clearSelection() }
    val dragController = rememberDragSelection(
        orderedKeys = state.items.map { it.fileId },
        selected = state.selected,
        onSelectionChange = viewModel::setSelection,
        visibleCells = { gridState.visibleCells() },
        viewport = { gridState.viewportBounds(bottomInsetPx = actionBarHeightPx.toFloat()) },
        scrollBy = { gridState.scrollBy(it) },
        onDragActiveChange = { dragging = it },
    )

    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) {
                // Синхронно: просмотр Vault не должен пережить уход в фон, даже в fade-out.
                viewer = null
                viewModel.relock()
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    LaunchedEffect(state.lockState) {
        viewer = vaultViewerSelection(state.lockState, viewer)
    }

    LaunchedEffect(state.lockState, activity) {
        if (state.lockState == VaultLockState.LOCKED) {
            activity?.let { viewModel.unlock(it, unlockTitle) }
        }
    }
    LaunchedEffect(state.snackbar) {
        val message = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(message)
        viewModel.snackbarShown()
    }

    CloudMediaTransitionLayout(viewer?.id) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.vault_title)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp, enabled = !state.busy) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            when (state.lockState) {
                VaultLockState.LOCKED -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        Icon(Icons.Outlined.Lock, contentDescription = null, modifier = Modifier.size(48.dp))
                        Text(stringResource(R.string.vault_locked_title))
                        TextButton(onClick = { activity?.let { viewModel.unlock(it, unlockTitle) } }) {
                            Text(unlockTitle)
                        }
                    }
                }
                VaultLockState.UNLOCKING -> LoadingIndicator(Modifier.align(Alignment.Center))
                VaultLockState.UNLOCKED -> {
                    if (state.items.isEmpty()) {
                        Text(
                            text = stringResource(R.string.vault_empty),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.align(Alignment.Center),
                        )
                    } else {
                        LazyVerticalGrid(
                            columns = GridCells.Adaptive(minSize = 120.dp),
                            modifier = Modifier.fillMaxSize().dragSelection(dragController) { id ->
                                if (state.busy) return@dragSelection
                                if (selecting) {
                                    viewModel.setSelection(state.selected.toggled(id))
                                } else {
                                    viewer = state.items.firstOrNull { it.fileId == id }?.toMediaAsset()
                                }
                            },
                            state = gridState,
                            userScrollEnabled = !dragging,
                        ) {
                            items(state.items, key = { it.fileId }) { item ->
                                VaultCell(item = item, selecting = selecting, selected = item.fileId in state.selected)
                            }
                        }
                        if (selecting) {
                            val count = state.selected.size
                            SelectionActionBar(
                                modifier = Modifier.align(Alignment.BottomCenter).onSizeChanged { actionBarHeightPx = it.height },
                                actions = buildList {
                                    add(SelectionAction(stringResource(R.string.vault_remove_selected, count), enabled = !state.busy) {
                                        viewModel.removeSelectedFromVault()
                                    })
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
            }
        }
    }

        // Вне UNLOCKED вьюер убирается из композиции сразу, без fade: полное фото не остаётся над экраном авторизации.
        if (state.lockState == VaultLockState.UNLOCKED) {
            CloudMediaViewer(
                assets = vaultAssets,
                selectedAsset = vaultViewerSelection(state.lockState, viewer),
                onDismiss = { viewer = null },
                infoEnabled = false,
                onDelete = { item -> viewModel.deleteFile(item.id) },
            )
        }
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
private fun VaultCell(
    item: VaultItem,
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
            model = item.previewUrl,
            isVideo = item.isVideo,
            contentDescription = item.fileName,
            modifier = Modifier.fillMaxSize().cloudMediaSharedElement(
                key = item.fileId,
                visible = LocalCloudMediaViewerAssetId.current != item.fileId,
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
