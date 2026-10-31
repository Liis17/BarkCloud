package com.barkfluff.BarkCloud.ui.components

import android.net.Uri
import com.barkfluff.BarkCloud.data.cache.LocalMediaImageModel
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import java.io.File

internal fun DeviceMedia.localPreviewModel(maxDimensionPx: Int) = LocalMediaImageModel(
    uri = uri.toString(),
    sourceId = "media:$folderKey:$uri",
    sourceVersion = "$generationModified:$dateModifiedSeconds:$sizeBytes",
    maxDimensionPx = maxDimensionPx,
    isVideo = isVideo,
)

internal fun File.localPreviewModel(
    maxDimensionPx: Int,
    isVideo: Boolean,
    modifiedAtMillis: Long = lastModified(),
    sizeBytes: Long = length(),
) = LocalMediaImageModel(
    uri = Uri.fromFile(this).toString(),
    sourceId = "file:$absolutePath",
    sourceVersion = "$modifiedAtMillis:$sizeBytes",
    maxDimensionPx = maxDimensionPx,
    isVideo = isVideo,
)
