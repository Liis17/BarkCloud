package com.barkfluff.BarkCloud.ui.shared

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.SharedFolderEntry
import com.barkfluff.BarkCloud.data.cloud.SharedWithMeEntry
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.data.MimeIcon
import com.barkfluff.BarkCloud.files.ui.formatDate
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@Composable
fun SharedWithMeScreen(
    onOpenFolder: (directoryId: String, name: String) -> Unit,
    onSnackbar: (String) -> Unit,
    viewModel: SharedWithMeViewModel = viewModel(factory = SharedWithMeViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val listState = rememberLazyListState()
    val context = LocalContext.current

    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }
    LaunchedEffect(state.snackbar) {
        state.snackbar?.let { onSnackbar(it); viewModel.snackbarShown() }
    }
    val shouldLoadMore by remember {
        derivedStateOf {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= state.items.size + state.folders.size - 5
        }
    }
    LaunchedEffect(shouldLoadMore) { if (shouldLoadMore) viewModel.loadMore() }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize(), state = listState) {
            items(state.items, key = { "f:${it.grantId}" }) { entry ->
                SharedFileRow(
                    entry = entry,
                    owner = state.owners[entry.ownerUserId],
                    isDownloading = state.downloadingFileId == entry.asset.id,
                    onDownload = {
                        viewModel.download(entry) { file ->
                            val mime = MimeIcon.mimeFor(entry.asset.fileName)
                            context.startActivity(FileShareHelper.buildOpenIntent(context, file, mime))
                        }
                    },
                )
            }
            items(state.folders, key = { "d:${it.grantId}" }) { entry ->
                SharedFolderRow(entry = entry, owner = state.owners[entry.ownerUserId], onOpen = { onOpenFolder(entry.directoryId, entry.name) })
            }
        }

        if (state.isLoading) {
            CircularProgressIndicator(Modifier.align(Alignment.Center))
        }
        if (state.isEmpty) {
            Text(
                text = stringResource(R.string.shared_with_me_empty),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.align(Alignment.Center),
            )
        }
    }
}

private fun displayName(user: User?, fallbackId: Long): String {
    if (user == null) return "#$fallbackId"
    val full = "${user.firstName} ${user.lastName}".trim()
    return full.ifBlank { user.username.ifBlank { "#$fallbackId" } }
}

@Composable
private fun SharedFileRow(
    entry: SharedWithMeEntry,
    owner: User?,
    isDownloading: Boolean,
    onDownload: () -> Unit,
) {
    val hasPreview = entry.asset.previews.isNotEmpty()
    ListItem(
        headlineContent = { Text(entry.asset.fileName, maxLines = 1) },
        supportingContent = {
            Text(
                displayName(owner, entry.ownerUserId) + " · " + formatDate(entry.sharedAtMillis),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        leadingContent = {
            if (hasPreview) {
                MediaThumb(
                    model = entry.asset.previewUrl(128),
                    isVideo = entry.asset.isVideo,
                    contentDescription = entry.asset.fileName,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                )
            } else {
                Icon(MimeIcon.iconFor(MimeIcon.mimeFor(entry.asset.fileName), entry.asset.fileName), contentDescription = null)
            }
        },
        trailingContent = {
            if (isDownloading) {
                CircularProgressIndicator(Modifier.size(24.dp))
            } else {
                IconButton(onClick = onDownload) {
                    Icon(Icons.Outlined.Download, contentDescription = stringResource(R.string.shared_download))
                }
            }
        },
    )
}

@Composable
private fun SharedFolderRow(
    entry: SharedFolderEntry,
    owner: User?,
    onOpen: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(entry.name, maxLines = 1) },
        supportingContent = {
            Text(
                displayName(owner, entry.ownerUserId) + " · " + formatDate(entry.sharedAtMillis),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null) },
        modifier = Modifier.clickable(onClick = onOpen),
    )
}
