package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.AlbumInfo
import barkcloud.files.FilesApiOuterClass.DirectoryInfo
import barkcloud.files.FilesApiOuterClass.FavoriteEntry
import barkcloud.files.FilesApiOuterClass.FileEntryDetailed
import barkcloud.files.FilesApiOuterClass.MediaKind
import barkcloud.files.FilesApiOuterClass.TrashEntry
import barkcloud.files.FilesApiOuterClass.UploadFileInfo
import com.google.protobuf.Timestamp

/** Protobuf Timestamp → epoch миллисекунды. */
fun Timestamp.toEpochMillis(): Long = seconds * 1000L + nanos / 1_000_000L

/** Epoch миллисекунды → protobuf Timestamp (для курсоров пагинации). */
fun Long.toTimestamp(): Timestamp = Timestamp.newBuilder()
    .setSeconds(this / 1000L)
    .setNanos(((this % 1000L) * 1_000_000L).toInt())
    .build()

/** Категория медиа (зеркалит `MediaKind`). */
enum class CloudMediaKind {
    OTHER, PHOTO, VIDEO, DOCUMENT, AUDIO;

    val isVideo: Boolean get() = this == VIDEO

    companion object {
        fun from(proto: MediaKind): CloudMediaKind = when (proto) {
            MediaKind.MEDIA_KIND_PHOTO -> PHOTO
            MediaKind.MEDIA_KIND_VIDEO -> VIDEO
            MediaKind.MEDIA_KIND_DOCUMENT -> DOCUMENT
            MediaKind.MEDIA_KIND_AUDIO -> AUDIO
            else -> OTHER
        }
    }
}

/** Превью определённой ширины. */
data class MediaPreview(val url: String, val width: Int)

/**
 * Медиа-файл облака (фото/видео/документ) с превью и метаданными.
 * [id] — это `file_id` блоба.
 */
data class MediaAsset(
    val id: String,
    val fileName: String,
    val fileSize: Long,
    val kind: CloudMediaKind,
    val previews: List<MediaPreview>,
    val createdAtMillis: Long,
) {
    val isVideo: Boolean get() = kind.isVideo

    /** Превью ближайшее к нужной ширине (или максимальное доступное). */
    fun previewUrl(preferredWidth: Int): String? {
        if (previews.isEmpty()) return null
        val sorted = previews.sortedBy { it.width }
        return (sorted.firstOrNull { it.width >= preferredWidth } ?: sorted.last()).url
    }

    companion object {
        fun from(info: UploadFileInfo): MediaAsset = MediaAsset(
            id = info.id,
            fileName = info.fileName,
            fileSize = info.fileSize,
            kind = CloudMediaKind.from(info.mediaKind),
            previews = info.previewsList.mapNotNull { p ->
                if (p.previewUrl.isEmpty()) null else MediaPreview(p.previewUrl, p.targetWidth)
            },
            createdAtMillis = if (info.hasCreatedAt()) info.createdAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница медиа-галереи с курсором пагинации. */
data class MediaPage(
    val items: List<MediaAsset>,
    val nextCursorCreatedAtMillis: Long?,
    val nextCursorFileId: String,
) {
    val hasMore: Boolean get() = nextCursorCreatedAtMillis != null
}

/** Папка облака (зеркалит `DirectoryInfo`). */
data class CloudDirectory(val id: String, val parentId: String, val name: String) {
    companion object {
        fun from(d: DirectoryInfo): CloudDirectory = CloudDirectory(d.id, d.parentId, d.name)
    }
}

/** Запись о файле в папке (зеркалит `FileEntryDetailed`). */
data class CloudFileEntry(
    val id: String,      // entry_id (ID записи в иерархии)
    val fileId: String,  // ID блоба
    val name: String,
    val asset: MediaAsset,
) {
    companion object {
        fun from(d: FileEntryDetailed): CloudFileEntry = CloudFileEntry(
            id = d.entry.id,
            fileId = d.entry.fileId,
            name = d.entry.name,
            asset = MediaAsset.from(d.file),
        )
    }
}

/** Содержимое папки. */
data class CloudListing(val subdirs: List<CloudDirectory>, val files: List<CloudFileEntry>)

/** Сегмент хлебных крошек. */
data class PathCrumb(val id: String, val name: String)

/** Запись в корзине (зеркалит `TrashEntry`). [id] — entry_id записи. */
data class TrashItem(
    val id: String,
    val fileId: String,
    val name: String,
    val asset: MediaAsset,
    val deletedAtMillis: Long,
    val purgeAtMillis: Long,
) {
    companion object {
        fun from(t: TrashEntry): TrashItem = TrashItem(
            id = t.entry.id,
            fileId = t.entry.fileId,
            name = t.entry.name,
            asset = MediaAsset.from(t.file),
            deletedAtMillis = if (t.hasDeletedAt()) t.deletedAt.toEpochMillis() else 0L,
            purgeAtMillis = if (t.hasPurgeAt()) t.purgeAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница корзины с курсором пагинации. */
data class TrashPage(
    val items: List<TrashItem>,
    val nextCursorDeletedAtMillis: Long?,
    val nextCursorEntryId: String,
) {
    val hasMore: Boolean get() = nextCursorDeletedAtMillis != null
}

/** Карточка альбома (зеркалит `AlbumInfo`). */
data class AlbumCard(
    val id: String,
    val name: String,
    val description: String,
    val coverPreviewUrl: String?,
    val coverFileId: String,
    val itemsCount: Int,
    val updatedAtMillis: Long,
) {
    companion object {
        fun from(a: AlbumInfo): AlbumCard = AlbumCard(
            id = a.id,
            name = a.name,
            description = a.description,
            coverPreviewUrl = a.coverPreviewUrl.ifEmpty { null },
            coverFileId = a.coverFileId,
            itemsCount = a.itemsCount,
            updatedAtMillis = if (a.hasUpdatedAt()) a.updatedAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница списка альбомов с курсором пагинации. */
data class AlbumPage(
    val albums: List<AlbumCard>,
    val nextCursorUpdatedAtMillis: Long?,
    val nextCursorAlbumId: String,
) {
    val hasMore: Boolean get() = nextCursorUpdatedAtMillis != null
}

/** Элемент списка избранного (зеркалит `FavoriteEntry`). */
data class FavoriteItem(val asset: MediaAsset, val favoritedAtMillis: Long) {
    companion object {
        fun from(f: FavoriteEntry): FavoriteItem = FavoriteItem(
            asset = MediaAsset.from(f.file),
            favoritedAtMillis = if (f.hasFavoritedAt()) f.favoritedAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница избранного с курсором пагинации. */
data class FavoritesPage(
    val items: List<FavoriteItem>,
    val nextCursorFavoritedAtMillis: Long?,
    val nextCursorFileId: String,
) {
    val hasMore: Boolean get() = nextCursorFavoritedAtMillis != null
}
