package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CloudQueue
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.formatSize
import com.barkfluff.BarkCloud.net.FileTransferService

@Composable
internal fun GalleryStorageCard(storage: FileTransferService.StorageInfo) {
    val context = LocalContext.current
    val colors = MaterialTheme.colorScheme
    val files = (storage.byType[UploadFileType.CLOUD_FILE] ?: 0L).coerceAtLeast(0)
    val avatars = (storage.byType[UploadFileType.USER_AVATAR] ?: 0L).coerceAtLeast(0)
    val other = (storage.used - files - avatars).coerceAtLeast(0)
    val categories = listOf(
        StorageCategory(stringResource(R.string.tab_files), files, colors.primary),
        StorageCategory(stringResource(R.string.gallery_storage_avatars), avatars, colors.secondary),
    ) + if (other > 0) listOf(
        StorageCategory(stringResource(R.string.gallery_storage_other), other, colors.tertiary),
    ) else emptyList()
    val usedLabel = formatSize(context, storage.used)
    val usage = if (storage.limit > 0) {
        stringResource(R.string.settings_storage_usage, usedLabel, formatSize(context, storage.limit))
    } else {
        stringResource(R.string.gallery_storage_unlimited, usedLabel)
    }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = MaterialTheme.shapes.large,
        color = colors.surfaceContainerHighest,
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.CloudQueue, contentDescription = null, modifier = Modifier.size(18.dp))
                Text(stringResource(R.string.settings_storage_title), style = MaterialTheme.typography.labelLarge)
            }
            Text(usage, style = MaterialTheme.typography.titleMedium)
            Canvas(Modifier.fillMaxWidth().height(10.dp).clip(CircleShape)) {
                drawRect(colors.surfaceVariant)
                // With no quota, show the composition of used space rather than a fictitious capacity.
                val denominator = maxOf(storage.limit.toDouble(), storage.used.toDouble(), categories.sumOf { it.bytes.toDouble() }, 1.0)
                var offset = 0f
                categories.forEach { category ->
                    val width = (size.width * category.bytes / denominator).toFloat()
                    val left = if (layoutDirection == LayoutDirection.Rtl) size.width - offset - width else offset
                    drawRect(category.color, Offset(left, 0f), Size(width, size.height))
                    offset += width
                }
            }
            categories.chunked(2).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                    row.forEach { category ->
                        Row(
                            modifier = Modifier.weight(1f).semantics(mergeDescendants = true) {},
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Box(Modifier.size(8.dp).background(category.color, CircleShape))
                            Column {
                                Text(category.label, style = MaterialTheme.typography.labelMedium)
                                Text(
                                    formatSize(context, category.bytes),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = colors.onSurfaceVariant,
                                )
                            }
                        }
                    }
                    if (row.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

private data class StorageCategory(val label: String, val bytes: Long, val color: Color)
