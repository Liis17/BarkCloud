package com.barkfluff.BarkCloud.ui.shared

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.PersonRemove
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
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import barkcloud.users.UsersApiOuterClass.User
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.OutgoingFolderShareGroup
import com.barkfluff.BarkCloud.data.cloud.OutgoingRecipient
import com.barkfluff.BarkCloud.data.cloud.OutgoingShareGroup
import com.barkfluff.BarkCloud.files.ui.formatDate
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@Composable
fun MyOutgoingSharesScreen(
    onSnackbar: (String) -> Unit,
    viewModel: MyOutgoingSharesViewModel = viewModel(factory = MyOutgoingSharesViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val listState = rememberLazyListState()

    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }
    LaunchedEffect(state.snackbar) {
        state.snackbar?.let { onSnackbar(it); viewModel.snackbarShown() }
    }
    val shouldLoadMore by remember {
        derivedStateOf {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= state.groups.size + state.folderGroups.size - 3
        }
    }
    LaunchedEffect(shouldLoadMore) { if (shouldLoadMore) viewModel.loadMore() }

    Box(Modifier.fillMaxSize()) {
        LazyColumn(Modifier.fillMaxSize(), state = listState) {
            if (state.groups.isNotEmpty()) {
                items(state.groups, key = { "f:${it.id}" }) { group ->
                    OutgoingFileGroupCard(
                        group = group,
                        users = state.users,
                        onRevoke = { grantId -> viewModel.revoke(grantId) },
                    )
                }
            }
            if (state.folderGroups.isNotEmpty()) {
                items(state.folderGroups, key = { "d:${it.id}" }) { group ->
                    OutgoingFolderGroupCard(
                        group = group,
                        users = state.users,
                        onRevoke = { grantId -> viewModel.revokeFolder(grantId) },
                    )
                }
            }
            if (state.isLoadingMore) {
                item {
                    Box(Modifier.fillMaxSize().padding(16.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator()
                    }
                }
            }
        }

        if (state.isLoading) {
            CircularProgressIndicator(Modifier.align(Alignment.Center))
        }
        if (state.isEmpty) {
            Text(
                text = stringResource(R.string.shared_outgoing_empty),
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
private fun OutgoingFileGroupCard(
    group: OutgoingShareGroup,
    users: Map<Long, User>,
    onRevoke: (String) -> Unit,
) {
    Box {
        ListItem(
            headlineContent = { Text(group.file.fileName, maxLines = 1) },
            leadingContent = {
                if (group.file.previews.isNotEmpty()) {
                    MediaThumb(
                        model = group.file.previewUrl(128),
                        isVideo = group.file.isVideo,
                        contentDescription = group.file.fileName,
                        modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                    )
                } else {
                    Icon(Icons.Outlined.Folder, contentDescription = null)
                }
            },
        )
    }
    group.recipients.forEach { recipient ->
        RecipientRow(recipient, users[recipient.recipientUserId], onRevoke)
    }
}

@Composable
private fun OutgoingFolderGroupCard(
    group: OutgoingFolderShareGroup,
    users: Map<Long, User>,
    onRevoke: (String) -> Unit,
) {
    ListItem(
        headlineContent = { Text(group.name, maxLines = 1) },
        leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null) },
    )
    group.recipients.forEach { recipient ->
        RecipientRow(recipient, users[recipient.recipientUserId], onRevoke)
    }
}

@Composable
private fun RecipientRow(
    recipient: OutgoingRecipient,
    user: User?,
    onRevoke: (String) -> Unit,
) {
    ListItem(
        headlineContent = { Text(displayName(user, recipient.recipientUserId)) },
        supportingContent = {
            Text(
                stringResource(R.string.shared_shared_at, formatDate(recipient.sharedAtMillis)),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        trailingContent = {
            IconButton(onClick = { onRevoke(recipient.grantId) }) {
                Icon(Icons.Outlined.PersonRemove, contentDescription = stringResource(R.string.shared_revoke_action))
            }
        },
        modifier = Modifier.padding(start = 24.dp),
    )
}
