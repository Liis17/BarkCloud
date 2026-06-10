package com.barkfluff.BarkCloud.ui.smartfolders

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
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
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.rememberRemoteOpener

@OptIn(ExperimentalMaterial3Api::class)
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
    val openRemote = rememberRemoteOpener()
    var viewer by remember { mutableStateOf<MediaAsset?>(null) }

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

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(title, maxLines = 1) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
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
                LazyVerticalGrid(
                    columns = GridCells.Fixed(3),
                    state = gridState,
                    modifier = Modifier.fillMaxSize(),
                ) {
                    items(state.items, key = { it.id }) { asset ->
                        SmartFolderMediaCell(asset = asset, onTap = { viewer = asset })
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
                CircularProgressIndicator(Modifier.align(Alignment.Center))
            }
        }
    }

    viewer?.let { asset ->
        CloudMediaViewer(
            asset = asset,
            onDismiss = { viewer = null },
            onPlayVideo = { openRemote(asset.id, asset.fileName) { } },
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun SmartFolderMediaCell(
    asset: MediaAsset,
    onTap: () -> Unit,
) {
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .padding(1.dp)
            .clip(MaterialTheme.shapes.small)
            .combinedClickable(onClick = onTap),
    ) {
        MediaThumb(
            model = asset.previewUrl(512),
            isVideo = asset.isVideo,
            contentDescription = asset.fileName,
            modifier = Modifier.fillMaxSize(),
        )
    }
}
