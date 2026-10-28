package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.net.FileTransferService
import com.barkfluff.BarkCloud.files.ui.formatSize
import com.barkfluff.BarkCloud.ui.components.RemoteImage
import com.barkfluff.BarkCloud.ui.upload.UploadJobRow
import com.barkfluff.BarkCloud.ui.upload.UploadQueueUiState

@Composable
internal fun GalleryAccountDialog(
    displayName: String?,
    username: String?,
    avatarUrl: String?,
    storageInfo: FileTransferService.StorageInfo?,
    storageLoading: Boolean,
    uploadState: UploadQueueUiState,
    reclaimableBytes: Long,
    onOpenUploads: () -> Unit,
    onFreeDeviceSpace: () -> Unit,
    onOpenProfile: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenTrash: () -> Unit,
    onDismiss: () -> Unit,
) {
    val scrollState: ScrollState = rememberScrollState()
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Surface(
            modifier = Modifier.fillMaxWidth(0.92f).heightIn(max = 720.dp),
            shape = MaterialTheme.shapes.extraLarge,
            color = MaterialTheme.colorScheme.surfaceContainerHigh,
            tonalElevation = 6.dp,
        ) {
            Column(
                modifier = Modifier.verticalScroll(scrollState).padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Surface(
                        modifier = Modifier.size(56.dp),
                        shape = CircleShape,
                        color = MaterialTheme.colorScheme.surfaceVariant,
                    ) {
                        if (avatarUrl != null) {
                            RemoteImage(
                                url = avatarUrl,
                                contentDescription = null,
                                normalize = true,
                                modifier = Modifier.size(56.dp).clip(CircleShape),
                            )
                        } else {
                            Icon(
                                Icons.Outlined.Person,
                                contentDescription = null,
                                modifier = Modifier.padding(14.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(
                            displayName?.takeIf { it.isNotBlank() }
                                ?: stringResource(R.string.gallery_account_profile_unavailable),
                            style = MaterialTheme.typography.titleMedium,
                        )
                        username?.takeIf { it.isNotBlank() }?.let {
                            Text("@$it", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    Surface(
                        shape = CircleShape,
                        color = MaterialTheme.colorScheme.secondaryContainer,
                        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                    ) {
                        Text(
                            stringResource(R.string.gallery_self_hosted),
                            modifier = Modifier.padding(horizontal = 10.dp, vertical = 6.dp),
                            style = MaterialTheme.typography.labelSmall,
                        )
                    }
                }

                HorizontalDivider()
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    when {
                        storageLoading -> CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                        storageInfo != null -> GalleryStorageCard(storageInfo)
                        else -> Text(
                            stringResource(R.string.gallery_account_storage_unavailable),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }

                HorizontalDivider()
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.gallery_uploads_title), style = MaterialTheme.typography.titleSmall)
                    if (uploadState.pendingJobs.isEmpty()) {
                        Text(
                            stringResource(R.string.gallery_uploads_empty),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    } else {
                        LazyColumn(
                            modifier = Modifier.fillMaxWidth().heightIn(max = 240.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            items(uploadState.pendingJobs, key = { it.id }) { job ->
                                UploadJobRow(job = job, modifier = Modifier.fillMaxWidth())
                            }
                        }
                    }
                    TextButton(onClick = onOpenUploads) {
                        Text(stringResource(R.string.gallery_open_uploads))
                    }
                }

                HorizontalDivider()
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilledTonalButton(
                        onClick = onFreeDeviceSpace,
                        modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp),
                        shape = MaterialTheme.shapes.large,
                    ) {
                        Icon(Icons.Outlined.DeleteOutline, contentDescription = null, modifier = Modifier.size(20.dp))
                        Spacer(Modifier.size(10.dp))
                        Text(stringResource(R.string.gallery_free_device_space), modifier = Modifier.weight(1f))
                        Text(formatSize(LocalContext.current, reclaimableBytes), style = MaterialTheme.typography.labelLarge)
                    }
                    Text(
                        stringResource(R.string.gallery_cleanup_cloud_kept),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                HorizontalDivider()
                Column {
                    TextButton(onClick = onOpenProfile) { Text(stringResource(R.string.settings_edit_profile)) }
                    TextButton(onClick = onOpenSettings) { Text(stringResource(R.string.settings_title)) }
                    TextButton(onClick = onOpenTrash) { Text(stringResource(R.string.tab_trash)) }
                }
                Spacer(Modifier.height(4.dp))
            }
        }
    }
}
