package com.barkfluff.BarkCloud.ui.components

import kotlinx.coroutines.CancellationException
import java.util.Locale

private val jpegViewRequiredExtensions = setOf(
    "heic", "heif", "svg", "tif", "tiff", "avif", "jxl",
    "dng", "cr2", "cr3", "nef", "nrw", "arw", "orf", "rw2",
)

internal enum class ViewerCacheKind {
    ORIGINAL,
    JPEG_VIEW,
}

internal data class CloudMediaActionVisibility(
    val share: Boolean,
    val copy: Boolean,
    val download: Boolean,
    val info: Boolean,
    val delete: Boolean,
)

internal fun cloudMediaActionVisibility(
    isVideo: Boolean,
    infoEnabled: Boolean,
    canDelete: Boolean,
): CloudMediaActionVisibility = CloudMediaActionVisibility(
    share = true,
    copy = !isVideo,
    download = true,
    info = infoEnabled,
    delete = canDelete,
)

internal enum class CloudMediaStoreCollection {
    IMAGES,
    VIDEOS,
}

internal data class CloudMediaStoreTarget(
    val collection: CloudMediaStoreCollection,
    val mimeType: String,
    val relativePath: String,
)

internal fun cloudMediaStoreTarget(isVideo: Boolean, mimeType: String): CloudMediaStoreTarget =
    CloudMediaStoreTarget(
        collection = if (isVideo) CloudMediaStoreCollection.VIDEOS else CloudMediaStoreCollection.IMAGES,
        mimeType = mimeType,
        relativePath = if (isVideo) "Movies/BarkCloud" else "Pictures/BarkCloud",
    )

internal fun viewerIndexAfterAssetListChange(
    previousIds: List<String>,
    currentId: String?,
    currentIds: List<String>,
): Int? {
    if (currentIds.isEmpty()) return null
    currentId?.let { id -> currentIds.indexOf(id).takeIf { it >= 0 }?.let { return it } }
    val previousIndex = currentId?.let(previousIds::indexOf)?.takeIf { it >= 0 } ?: 0
    return previousIndex.coerceAtMost(currentIds.lastIndex)
}

internal fun viewerCacheKindAfterDecodeFailure(useJpegView: Boolean): ViewerCacheKind =
    if (useJpegView) ViewerCacheKind.JPEG_VIEW else ViewerCacheKind.ORIGINAL

internal suspend fun invalidateViewerCacheSafely(invalidate: suspend () -> Unit) {
    try {
        invalidate()
    } catch (error: CancellationException) {
        throw error
    } catch (_: Exception) {
    }
}

internal fun requiresJpegView(fileName: String, hasJpegView: Boolean): Boolean {
    if (!hasJpegView) return false
    val extension = fileName.substringAfterLast('.', "").lowercase(Locale.ROOT)
    return extension in jpegViewRequiredExtensions
}
