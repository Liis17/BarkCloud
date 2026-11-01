package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
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
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.formatSize
import com.barkfluff.BarkCloud.net.FileTransferService

@Composable
internal fun GalleryStorageCard(storage: FileTransferService.StorageInfo?, loading: Boolean) {
    val context = LocalContext.current
    val colors = MaterialTheme.colorScheme
    val files = storage?.let { (it.byType[UploadFileType.CLOUD_FILE] ?: 0L).coerceAtLeast(0L) } ?: 0L
    val avatars = storage?.let { (it.byType[UploadFileType.USER_AVATAR] ?: 0L).coerceAtLeast(0L) } ?: 0L
    val other = storage?.let { (it.used - files - avatars).coerceAtLeast(0L) } ?: 0L
    val categories = listOf(
        StorageCategory(stringResource(R.string.tab_files), files, colors.primary),
        StorageCategory(stringResource(R.string.gallery_storage_avatars), avatars, colors.secondary),
        StorageCategory(stringResource(R.string.gallery_storage_other), other, colors.tertiary),
    )
    val usage = when {
        storage == null -> null
        storage.limit > 0 -> stringResource(
            R.string.settings_storage_usage,
            formatSize(context, storage.used),
            formatSize(context, storage.limit),
        )
        else -> stringResource(R.string.gallery_storage_unlimited, formatSize(context, storage.used))
    }

    Surface(
        modifier = Modifier.fillMaxWidth().testTag("gallery-storage-card"),
        shape = MaterialTheme.shapes.large,
        color = colors.surfaceContainerHighest,
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.CloudQueue, contentDescription = null, modifier = Modifier.size(18.dp))
                Text(stringResource(R.string.settings_storage_title), style = MaterialTheme.typography.labelLarge)
            }
            when {
                loading -> StorageSkeletonLine(
                    style = MaterialTheme.typography.titleMedium,
                    accessibleLabel = stringResource(R.string.gallery_storage_loading),
                )
                usage != null -> Text(
                    usage,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    softWrap = false,
                    overflow = TextOverflow.Ellipsis,
                )
                else -> Text(
                    stringResource(R.string.gallery_account_storage_unavailable),
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    softWrap = false,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Canvas(Modifier.fillMaxWidth().height(10.dp).clip(CircleShape)) {
                drawRect(colors.surfaceVariant)
                if (storage != null) {
                    val denominator = maxOf(
                        storage.limit.toDouble(),
                        storage.used.toDouble(),
                        categories.sumOf { it.bytes.toDouble() },
                        1.0,
                    )
                    var offset = 0f
                    categories.forEach { category ->
                        val width = (size.width * category.bytes / denominator).toFloat()
                        val left = if (layoutDirection == LayoutDirection.Rtl) size.width - offset - width else offset
                        drawRect(category.color, Offset(left, 0f), Size(width, size.height))
                        offset += width
                    }
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).testTag("gallery-storage-categories"),
                horizontalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                categories.forEach { category ->
                    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(8.dp).background(category.color, CircleShape))
                            Text(
                                category.label,
                                style = MaterialTheme.typography.labelMedium,
                                maxLines = 1,
                                softWrap = false,
                            )
                        }
                        if (loading) {
                            StorageSkeletonLine(MaterialTheme.typography.bodySmall)
                        } else {
                            Text(
                                storage?.let { formatSize(context, category.bytes) } ?: "—",
                                style = MaterialTheme.typography.bodySmall,
                                color = colors.onSurfaceVariant,
                                maxLines = 1,
                                softWrap = false,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun StorageSkeletonLine(style: TextStyle, accessibleLabel: String? = null) {
    val color = MaterialTheme.colorScheme.surfaceVariant
    Text(
        text = "888.8 GB",
        modifier = Modifier
            .drawBehind { drawRoundRect(color, cornerRadius = CornerRadius(4.dp.toPx())) }
            .clearAndSetSemantics {
                accessibleLabel?.let { contentDescription = it }
            },
        style = style,
        color = Color.Transparent,
        maxLines = 1,
        softWrap = false,
    )
}

private data class StorageCategory(val label: String, val bytes: Long, val color: Color)
