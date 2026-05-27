package com.barkfluff.BarkCloud.ui.trash

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.DeleteForever
import androidx.compose.material.icons.outlined.Restore
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.TrashItem
import com.barkfluff.BarkCloud.files.ui.formatDate
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TrashScreen(
    viewModel: TrashViewModel = viewModel(factory = TrashViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val listState = rememberLazyListState()
    var showEmptyConfirm by remember { mutableStateOf(false) }

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
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text(stringResource(R.string.tab_trash)) },
                actions = {
                    if (state.items.isNotEmpty()) {
                        IconButton(onClick = { showEmptyConfirm = true }) {
                            Icon(Icons.Outlined.DeleteForever, contentDescription = stringResource(R.string.trash_empty_action))
                        }
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            PullToRefreshBox(
                isRefreshing = state.isRefreshing,
                onRefresh = viewModel::reload,
                modifier = Modifier.fillMaxSize(),
            ) {
                LazyColumn(Modifier.fillMaxSize(), state = listState) {
                    items(state.items, key = { it.id }) { item ->
                        TrashRow(
                            item = item,
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
                    Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.3f)),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator() }
            }
        }
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

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TrashRow(
    item: TrashItem,
    onRestore: () -> Unit,
    onDelete: () -> Unit,
) {
    val dismissState = rememberSwipeToDismissBoxState(
        confirmValueChange = { value ->
            when (value) {
                SwipeToDismissBoxValue.StartToEnd -> { onRestore(); false }
                SwipeToDismissBoxValue.EndToStart -> { onDelete(); false }
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
        val hasPreview = item.asset.previews.isNotEmpty()
        ListItem(
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
                        model = item.asset.previewUrl(128),
                        isVideo = item.asset.isVideo,
                        contentDescription = item.name,
                        modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                    )
                } else {
                    Icon(Icons.Outlined.DeleteForever, contentDescription = null)
                }
            },
        )
    }
}
