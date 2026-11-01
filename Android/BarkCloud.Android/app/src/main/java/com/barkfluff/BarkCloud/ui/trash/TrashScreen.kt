package com.barkfluff.BarkCloud.ui.trash

import android.view.HapticFeedbackConstants
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.DeleteForever
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material.icons.outlined.Restore
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.TrashItem
import com.barkfluff.BarkCloud.files.ui.formatDate
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.RefreshableContentSurface
import com.barkfluff.BarkCloud.ui.selection.SelectionAction
import com.barkfluff.BarkCloud.ui.selection.SelectionActionBar
import com.barkfluff.BarkCloud.ui.selection.dragSelection
import com.barkfluff.BarkCloud.ui.selection.rememberDragSelection
import com.barkfluff.BarkCloud.ui.selection.toggled
import com.barkfluff.BarkCloud.ui.selection.viewportBounds
import com.barkfluff.BarkCloud.ui.selection.visibleRows

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun TrashScreen(
    onNavigateUp: () -> Unit,
    viewModel: TrashViewModel = viewModel(factory = TrashViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val listState = rememberLazyListState()
    var showEmptyConfirm by remember { mutableStateOf(false) }
    var showDeleteSelected by remember { mutableStateOf(false) }
    var dragging by remember { mutableStateOf(false) }
    var actionBarHeightPx by remember { mutableIntStateOf(0) }
    val selecting = state.selected.isNotEmpty()
    BackHandler(enabled = selecting || state.isProcessing) { if (!state.isProcessing) viewModel.clearSelection() }
    val dragController = rememberDragSelection(
        orderedKeys = state.items.map { it.id },
        selected = state.selected,
        onSelectionChange = viewModel::setSelection,
        visibleCells = { listState.visibleRows() },
        viewport = { listState.viewportBounds(bottomInsetPx = actionBarHeightPx.toFloat()) },
        scrollBy = { listState.scrollBy(it) },
        onDragActiveChange = { dragging = it },
    )

    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }
    val shouldLoadMore by remember {
        derivedStateOf {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= state.items.size - 5
        }
    }
    LaunchedEffect(shouldLoadMore) { if (shouldLoadMore) viewModel.loadMore() }

    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            RefreshableContentSurface(
                onRefresh = viewModel::reloadAndWait,
                header = {
                    CenterAlignedTopAppBar(
                        title = { Text(stringResource(R.string.tab_trash)) },
                        windowInsets = WindowInsets(0, 0, 0, 0),
                        navigationIcon = {
                            IconButton(onClick = onNavigateUp) {
                                Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = stringResource(R.string.files_back))
                            }
                        },
                        actions = {
                            if (state.items.isNotEmpty()) {
                                IconButton(onClick = { showEmptyConfirm = true }) {
                                    Icon(Icons.Outlined.DeleteForever, contentDescription = stringResource(R.string.trash_empty_action))
                                }
                            }
                        },
                    )
                },
                modifier = Modifier.fillMaxSize(),
            ) {
                LazyColumn(
                    Modifier.fillMaxSize().dragSelection(dragController) { id ->
                        if (selecting && !state.isProcessing) viewModel.setSelection(state.selected.toggled(id))
                    },
                    state = listState,
                    userScrollEnabled = !dragging,
                ) {
                    items(state.items, key = { it.id }) { item ->
                        TrashRow(
                            item = item,
                            selecting = selecting,
                            selected = item.id in state.selected,
                            onRestore = { viewModel.restore(item.id) },
                            onDelete = { viewModel.deleteForever(item.id) },
                        )
                    }
                }
            }

            if (state.isEmpty) {
                Text(
                    text = stringResource(R.string.trash_empty_list),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
            if (state.isProcessing) {
                Box(
                    Modifier.fillMaxSize().background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.3f)),
                    contentAlignment = Alignment.Center,
                ) { LoadingIndicator() }
            }
            if (selecting) {
                val count = state.selected.size
                SelectionActionBar(
                    modifier = Modifier.align(Alignment.BottomCenter).onSizeChanged { actionBarHeightPx = it.height },
                    actions = buildList {
                        add(SelectionAction(stringResource(R.string.trash_restore_selected, count), enabled = !state.isProcessing) {
                            viewModel.restoreSelected()
                        })
                        add(SelectionAction(stringResource(R.string.trash_delete_selected, count), enabled = !state.isProcessing) {
                            showDeleteSelected = true
                        })
                        add(SelectionAction(stringResource(R.string.common_cancel), enabled = !state.isProcessing) {
                            viewModel.clearSelection()
                        })
                    },
                )
            }
        }
    }

    if (showDeleteSelected) {
        val count = state.selected.size
        AlertDialog(
            onDismissRequest = { showDeleteSelected = false },
            title = { Text(stringResource(R.string.trash_delete_selected_title)) },
            text = { Text(stringResource(R.string.trash_delete_selected_message, count)) },
            confirmButton = {
                TextButton(onClick = { showDeleteSelected = false; viewModel.deleteSelectedForever() }) {
                    Text(stringResource(R.string.trash_delete_selected, count), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteSelected = false }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }

    if (showEmptyConfirm) {
        AlertDialog(
            onDismissRequest = { showEmptyConfirm = false },
            title = { Text(stringResource(R.string.trash_empty_title)) },
            text = { Text(stringResource(R.string.trash_empty_message)) },
            confirmButton = {
                TextButton(onClick = { showEmptyConfirm = false; viewModel.emptyTrash() }) {
                    Text(stringResource(R.string.trash_empty_action), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showEmptyConfirm = false }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }
}

/**
 * При выборе строка без свайпа: горизонтальный дрейф жеста диапазона не может запустить
 * восстановление или безвозвратное удаление.
 */
@Composable
private fun TrashRow(
    item: TrashItem,
    selecting: Boolean,
    selected: Boolean,
    onRestore: () -> Unit,
    onDelete: () -> Unit,
) {
    if (selecting) {
        TrashRowContent(item = item, selecting = true, selected = selected)
    } else {
        SwipeableTrashRow(item = item, onRestore = onRestore, onDelete = onDelete)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SwipeableTrashRow(
    item: TrashItem,
    onRestore: () -> Unit,
    onDelete: () -> Unit,
) {
    val view = LocalView.current
    val dismissState = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            when (value) {
                SwipeToDismissBoxValue.StartToEnd -> { view.performHapticFeedback(HapticFeedbackConstants.CONFIRM); onRestore(); false }
                SwipeToDismissBoxValue.EndToStart -> { view.performHapticFeedback(HapticFeedbackConstants.CONFIRM); onDelete(); false }
                else -> false
            }
        },
    )
    SwipeToDismissBox(
        state = dismissState,
        backgroundContent = {
            val isRestore = dismissState.dismissDirection == SwipeToDismissBoxValue.StartToEnd
            val color = if (isRestore) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.errorContainer
            Box(
                Modifier.fillMaxSize().background(color).padding(horizontal = 20.dp),
                contentAlignment = if (isRestore) Alignment.CenterStart else Alignment.CenterEnd,
            ) {
                Icon(
                    if (isRestore) Icons.Outlined.Restore else Icons.Outlined.DeleteForever,
                    contentDescription = null,
                )
            }
        },
    ) {
        TrashRowContent(item = item, selecting = false, selected = false)
    }
}

@Composable
private fun TrashRowContent(
    item: TrashItem,
    selecting: Boolean,
    selected: Boolean,
) {
    val hasPreview = item.asset.previews.isNotEmpty()
    // Фон строки совпадает с подложкой: свайп-фон SwipeToDismissBox рисуется под строкой целиком.
    ListItem(
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        headlineContent = { Text(item.name, maxLines = 1) },
        supportingContent = {
            Text(
                text = stringResource(R.string.trash_purge_at, formatDate(item.purgeAtMillis)),
                color = MaterialTheme.colorScheme.error,
            )
        },
        leadingContent = {
            if (hasPreview) {
                MediaThumb(
                    model = item.asset.previewModel(128),
                    isVideo = item.asset.isVideo,
                    contentDescription = item.name,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                )
            } else {
                Icon(Icons.Outlined.DeleteForever, contentDescription = null)
            }
        },
        trailingContent = if (selecting) {
            {
                Icon(
                    if (selected) Icons.Filled.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                    contentDescription = null,
                    tint = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        } else {
            null
        },
    )
}
