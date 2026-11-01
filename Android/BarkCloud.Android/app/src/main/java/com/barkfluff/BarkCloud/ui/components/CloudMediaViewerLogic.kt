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

/**
 * Состояние пагинации источника. [pagesCompleted] — монотонный счётчик успешно завершённых страниц: растёт и для
 * страницы без новых медиа (документы, дубликаты) и не растёт при сбое. Это устойчивый сигнал прогресса, а не булев
 * флаг загрузки: быстрые переходы false→true→false между наблюдениями не схлопываются в ту же пару значений.
 */
data class ViewerPagination(val hasMore: Boolean, val isLoading: Boolean, val pagesCompleted: Int = 0) {
    companion object {
        val NONE = ViewerPagination(hasMore = false, isLoading = false, pagesCompleted = 0)
    }
}

/** Последний запрос следующей страницы: позиция, с которой он сделан, и прогресс источника на момент запроса. */
internal data class ViewerPageRequest(val settledIndex: Int, val pagesCompleted: Int)

/**
 * Решение эффекта подгрузки просмотрщика (состояние держит composable между запусками эффекта).
 * Автоматический повтор возможен только после успешно завершённой страницы (рост [ViewerPagination.pagesCompleted]).
 * Сбой прогресс не меняет, поэтому повторов без участия пользователя нет. Навигация на другую страницу у края —
 * явный повтор пользователя: новый запрос допустим, даже если прогресс не менялся.
 */
internal class ViewerPageRequestTracker(private val prefetchDistance: Int = 3) {
    private var last: ViewerPageRequest? = null

    fun shouldRequest(settledIndex: Int, itemCount: Int, pagination: ViewerPagination): Boolean {
        if (!pagination.hasMore || pagination.isLoading) return false
        if (!shouldLoadMoreAtPagerEdge(settledIndex, itemCount, prefetchDistance)) return false
        val previous = last
        val allowed = previous == null ||
            previous.pagesCompleted != pagination.pagesCompleted ||
            previous.settledIndex != settledIndex
        if (allowed) last = ViewerPageRequest(settledIndex, pagination.pagesCompleted)
        return allowed
    }
}

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
