package com.barkfluff.BarkCloud.ui.vault

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.VisibilityOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.vault.VaultItem
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewer
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.rememberRemoteOpener

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VaultScreen(
    onNavigateUp: () -> Unit,
    viewModel: VaultViewModel = viewModel(factory = VaultViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val activity = context as? FragmentActivity
    val unlockTitle = stringResource(R.string.vault_unlock_button)
    var viewer by remember { mutableStateOf<MediaAsset?>(null) }
    val openRemote = rememberRemoteOpener()

    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) viewModel.relock()
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    LaunchedEffect(state.lockState, activity) {
        if (state.lockState == VaultLockState.LOCKED) {
            activity?.let { viewModel.unlock(it, unlockTitle) }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.vault_title)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
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
                VaultLockState.UNLOCKING -> CircularProgressIndicator(Modifier.align(Alignment.Center))
                VaultLockState.UNLOCKED -> {
                    if (state.items.isEmpty()) {
                        Text(
                            text = stringResource(R.string.vault_empty),
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.align(Alignment.Center),
                        )
                    } else {
                        LazyVerticalGrid(columns = GridCells.Fixed(3), modifier = Modifier.fillMaxSize()) {
                            items(state.items, key = { it.fileId }) { item ->
                                VaultCell(
                                    item = item,
                                    onTap = { viewer = item.toMediaAsset() },
                                    onRemove = { viewModel.remove(item.fileId) },
                                )
                            }
                        }
                    }
                }
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
private fun VaultCell(
    item: VaultItem,
    onTap: () -> Unit,
    onRemove: () -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .padding(1.dp)
            .clip(MaterialTheme.shapes.small)
            .combinedClickable(onClick = onTap, onLongClick = { menuOpen = true }),
    ) {
        MediaThumb(
            model = item.previewUrl,
            isVideo = item.isVideo,
            contentDescription = item.fileName,
            modifier = Modifier.fillMaxSize(),
        )
        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
            DropdownMenuItem(
                text = { Text(stringResource(R.string.vault_remove)) },
                leadingIcon = { Icon(Icons.Outlined.VisibilityOff, contentDescription = null) },
                onClick = { menuOpen = false; onRemove() },
            )
        }
    }
}
