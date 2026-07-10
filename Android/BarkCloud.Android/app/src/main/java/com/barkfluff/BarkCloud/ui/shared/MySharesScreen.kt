package com.barkfluff.BarkCloud.ui.shared

import android.content.Intent
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.PublicShareItem
import com.barkfluff.BarkCloud.data.cloud.PublicShareKind
import com.barkfluff.BarkCloud.data.cloud.publicUrl
import com.barkfluff.BarkCloud.files.ui.formatDate
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MySharesScreen(
    onSnackbar: (String) -> Unit,
    viewModel: MySharesViewModel = viewModel(factory = MySharesViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val context = LocalContext.current

    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }
    LaunchedEffect(state.snackbar) {
        state.snackbar?.let { onSnackbar(it); viewModel.snackbarShown() }
    }

    Box(Modifier.fillMaxSize()) {
        PullToRefreshBox(
            isRefreshing = state.isRefreshing,
            onRefresh = viewModel::reload,
            modifier = Modifier.fillMaxSize(),
        ) {
            LazyColumn(Modifier.fillMaxSize()) {
                items(state.items, key = { it.id }) { item ->
                    PublicShareRow(
                        item = item,
                        onShareLink = {
                            val url = item.publicUrl()
                            if (url == null) {
                                onSnackbar(context.getString(R.string.shared_download_failed))
                            } else {
                                val intent = Intent(Intent.ACTION_SEND).apply {
                                    type = "text/plain"
                                    putExtra(Intent.EXTRA_TEXT, url)
                                }
                                context.startActivity(Intent.createChooser(intent, null))
                            }
                        },
                        onRevoke = { viewModel.revoke(item) },
                    )
                }
            }
        }

        if (state.isLoading) {
            CircularProgressIndicator(Modifier.align(Alignment.Center))
        }
        if (state.isEmpty) {
            Text(
                text = stringResource(R.string.shared_my_public_empty),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.align(Alignment.Center),
            )
        }
    }
}

@Composable
private fun PublicShareRow(
    item: PublicShareItem,
    onShareLink: () -> Unit,
    onRevoke: () -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }
    ListItem(
        headlineContent = { Text(item.name, maxLines = 1) },
        supportingContent = {
            Text(
                text = stringResource(R.string.shared_click_count, item.clickCount) + " · " + formatDate(item.createdAtMillis),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        leadingContent = {
            if (item.previewUrl != null) {
                MediaThumb(
                    model = item.previewUrl,
                    isVideo = item.isVideo,
                    contentDescription = item.name,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                )
            } else {
                Icon(
                    when (item.kind) {
                        PublicShareKind.FILE -> Icons.Outlined.Link
                        PublicShareKind.FOLDER -> Icons.Outlined.Folder
                        PublicShareKind.ALBUM -> Icons.Outlined.PhotoLibrary
                    },
                    contentDescription = null,
                )
            }
        },
        trailingContent = {
            Box {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(Icons.Outlined.MoreVert, contentDescription = null)
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.shared_share_link_action)) },
                        leadingIcon = { Icon(Icons.Outlined.Share, contentDescription = null) },
                        onClick = { menuOpen = false; onShareLink() },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.shared_revoke_action), color = MaterialTheme.colorScheme.error) },
                        onClick = { menuOpen = false; onRevoke() },
                    )
                }
            }
        },
    )
}
