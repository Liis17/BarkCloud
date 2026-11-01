package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cloud.CloudMediaOrigin
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
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
    val album: Boolean,
    val favorite: Boolean,
)

/** Общий файл не вызывает собственные info/delete/album/favorite API, даже если панель свойств открыта. */
internal fun cloudMediaActionVisibility(
    isVideo: Boolean,
    infoEnabled: Boolean,
    canDelete: Boolean,
    origin: CloudMediaOrigin = CloudMediaOrigin.OWN,
): CloudMediaActionVisibility {
    val own = origin == CloudMediaOrigin.OWN
    return CloudMediaActionVisibility(
        share = true,
        copy = !isVideo,
        download = true,
        info = own && infoEnabled,
        delete = own && canDelete,
        album = own,
        favorite = own,
    )
}

/** Один и тот же общий файл из нескольких источников показывается в пейджере один раз. */
internal fun distinctViewerAssets(assets: List<MediaAsset>): List<MediaAsset> = assets.distinctBy { it.viewerId }

/** Запрос следующей страницы, когда пользователь подходит к концу уже загруженного списка. */
internal fun shouldLoadMoreAtPagerEdge(settledIndex: Int, itemCount: Int, prefetchDistance: Int = 3): Boolean =
    itemCount > 0 && settledIndex >= itemCount - prefetchDistance

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

/** Состояние пагинации источника, которым кормится просмотрщик: есть ли следующая страница и идёт ли загрузка. */
data class ViewerPagination(val hasMore: Boolean, val isLoading: Boolean) {
    companion object {
        val NONE = ViewerPagination(hasMore = false, isLoading = false)
    }
}

/**
 * Нужна ли следующая страница. Решение зависит от позиции и состояния источника, а не от числа медиа:
 * страница из одних документов или дубликатов не меняет размер списка, но всё равно должна продолжить подгрузку.
 * Запрос, пришедший во время загрузки, повторяется после её окончания (значения пересчитываются).
 */
internal fun shouldRequestNextPage(
    settledIndex: Int,
    itemCount: Int,
    pagination: ViewerPagination,
    prefetchDistance: Int = 3,
): Boolean = pagination.hasMore && !pagination.isLoading &&
    shouldLoadMoreAtPagerEdge(settledIndex, itemCount, prefetchDistance)

/** Представление фото, которое грузится в просмотрщике: полноразмерный оригинал или JPEG-вид. */
internal enum class ViewerRepresentation { ORIGINAL, JPEG_VIEW }

/**
 * Автомат восстановления загрузки фото в одной генерации (до явного Retry).
 * Каждое представление пробуется не более одного раза: сетевой сбой и ошибка декодирования одинаково
 * помечают его проваленным. Когда все кандидаты провалены, [current] = null — терминальная ошибка, превью остаётся.
 * Автоматического возврата к уже проваленному представлению нет; новая генерация начинается только через Retry.
 */
internal data class PhotoRecovery(
    val candidates: List<ViewerRepresentation>,
    val failed: Set<ViewerRepresentation> = emptySet(),
) {
    val current: ViewerRepresentation? get() = candidates.firstOrNull { it !in failed }

    fun afterFailure(representation: ViewerRepresentation): PhotoRecovery = copy(failed = failed + representation)
}

/** Порядок: JPEG первым только для форматов, которые без JPEG-вида не показать; иначе оригинал первым. */
internal fun photoRecoveryFor(requiresJpegView: Boolean, hasJpegView: Boolean): PhotoRecovery = PhotoRecovery(
    candidates = when {
        !hasJpegView -> listOf(ViewerRepresentation.ORIGINAL)
        requiresJpegView -> listOf(ViewerRepresentation.JPEG_VIEW, ViewerRepresentation.ORIGINAL)
        else -> listOf(ViewerRepresentation.ORIGINAL, ViewerRepresentation.JPEG_VIEW)
    },
)
