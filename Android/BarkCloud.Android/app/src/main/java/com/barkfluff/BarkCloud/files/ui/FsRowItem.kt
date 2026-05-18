package com.barkfluff.BarkCloud.files.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.data.MimeIcon
import com.barkfluff.BarkCloud.files.domain.FsEntry

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun FsRowItem(
    entry: FsEntry,
    selected: Boolean,
    selectionActive: Boolean,
    onPrimaryClick: () -> Unit,
    onLongPress: () -> Unit,
    onLeadingClick: () -> Unit,
    onActionRename: () -> Unit,
    onActionDelete: () -> Unit,
    onActionCopy: () -> Unit,
    onActionMove: () -> Unit,
    onActionOpen: () -> Unit,
    onActionShare: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    var menuOpen by remember { mutableStateOf(false) }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .fillMaxWidth()
            .combinedClickable(
                onClick = onPrimaryClick,
                onLongClick = onLongPress,
            )
            .padding(horizontal = 8.dp, vertical = 6.dp),
    ) {
        Box(
            modifier = Modifier
                .size(48.dp)
                .clip(RoundedCornerShape(12.dp))
                .background(MaterialTheme.colorScheme.surfaceContainerHighest)
                .clickable(onClick = onLeadingClick),
            contentAlignment = Alignment.Center,
        ) {
            when {
                selectionActive && selected -> Icon(
                    imageVector = Icons.Filled.CheckCircle,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(28.dp),
                )
                selectionActive -> Icon(
                    imageVector = Icons.Outlined.RadioButtonUnchecked,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(28.dp),
                )
                else -> RowThumbnail(entry)
            }
        }

        Spacer(modifier = Modifier.width(12.dp))

        androidx.compose.foundation.layout.Column(
            modifier = Modifier.weight(1f),
        ) {
            Text(
                text = entry.name,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
            )
            val supporting = when (entry) {
                is FsEntry.Directory -> formatChildCount(context, entry.childCount)
                is FsEntry.File -> formatSize(context, entry.sizeBytes) + " · " + formatDate(entry.lastModified)
            }
            Text(
                text = supporting,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        Box {
            IconButton(onClick = { menuOpen = true }) {
                Icon(
                    imageVector = Icons.Outlined.MoreVert,
                    contentDescription = stringResource(R.string.files_action_more),
                )
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                when (entry) {
                    is FsEntry.File -> {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.files_action_open)) },
                            onClick = { menuOpen = false; onActionOpen() },
                        )
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.files_action_share)) },
                            onClick = { menuOpen = false; onActionShare() },
                        )
                    }
                    is FsEntry.Directory -> {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.files_action_open)) },
                            onClick = { menuOpen = false; onActionOpen() },
                        )
                    }
                }
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.files_action_rename)) },
                    onClick = { menuOpen = false; onActionRename() },
                )
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.files_action_copy)) },
                    onClick = { menuOpen = false; onActionCopy() },
                )
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.files_action_move)) },
                    onClick = { menuOpen = false; onActionMove() },
                )
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.files_action_delete)) },
                    onClick = { menuOpen = false; onActionDelete() },
                )
            }
        }
    }
}

@Composable
private fun RowThumbnail(entry: FsEntry) {
    when (entry) {
        is FsEntry.Directory -> Icon(
            imageVector = MimeIcon.folderIcon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.primary,
            modifier = Modifier.size(28.dp),
        )
        is FsEntry.File -> {
            val thumbModel = rememberThumbnailModel(entry)
            if (thumbModel != null) {
                AsyncImage(
                    model = thumbModel,
                    contentDescription = null,
                    modifier = Modifier
                        .size(48.dp)
                        .clip(RoundedCornerShape(12.dp)),
                )
            } else {
                Icon(
                    imageVector = MimeIcon.iconFor(entry.mimeType, entry.name),
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(28.dp),
                )
            }
        }
    }
}
