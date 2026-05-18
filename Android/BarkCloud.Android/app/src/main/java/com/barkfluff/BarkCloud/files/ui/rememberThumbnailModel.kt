package com.barkfluff.BarkCloud.files.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import coil3.request.ImageRequest
import coil3.request.crossfade
import com.barkfluff.BarkCloud.files.domain.FsEntry
import java.io.File

@Composable
fun rememberThumbnailModel(entry: FsEntry.File): ImageRequest? {
    if (!entry.mimeType.startsWith("image/") && !entry.mimeType.startsWith("video/")) {
        return null
    }
    val context = LocalContext.current
    return remember(entry.path, entry.lastModified) {
        ImageRequest.Builder(context)
            .data(File(entry.path))
            .crossfade(true)
            .build()
    }
}
