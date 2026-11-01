package com.barkfluff.BarkCloud.files.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import com.barkfluff.BarkCloud.data.cache.LocalMediaImageModel
import com.barkfluff.BarkCloud.files.domain.FsEntry
import com.barkfluff.BarkCloud.ui.components.localPreviewModel
import java.io.File

@Composable
fun rememberThumbnailModel(entry: FsEntry.File): LocalMediaImageModel? {
    if (!entry.mimeType.startsWith("image/") && !entry.mimeType.startsWith("video/")) {
        return null
    }
    return remember(entry.path, entry.lastModified, entry.sizeBytes, entry.mimeType) {
        File(entry.path).localPreviewModel(
            maxDimensionPx = 256,
            isVideo = entry.mimeType.startsWith("video/"),
            modifiedAtMillis = entry.lastModified,
            sizeBytes = entry.sizeBytes,
        )
    }
}
