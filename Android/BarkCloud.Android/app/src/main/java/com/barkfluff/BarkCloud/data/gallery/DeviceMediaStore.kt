package com.barkfluff.BarkCloud.data.gallery

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** Локальный медиа-файл устройства (фото/видео) из MediaStore. */
data class DeviceMedia(
    val id: Long,
    val uri: Uri,
    val isVideo: Boolean,
    val name: String,
    val dateTakenMillis: Long,
    val dateModifiedSeconds: Long,
    val sizeBytes: Long,
    val folderKey: String = "",
    val folderName: String = "",
    val mimeType: String? = null,
) {
    val mediaKey: String get() = "${if (isVideo) "video" else "image"}:$id"
}

/** Чтение медиатеки устройства через MediaStore (фото + видео, от новых к старым). */
object DeviceMediaStore {

    private val projection = arrayOf(
        MediaStore.MediaColumns._ID,
        MediaStore.MediaColumns.DISPLAY_NAME,
        MediaStore.MediaColumns.DATE_ADDED,
        MediaStore.MediaColumns.DATE_MODIFIED,
        MediaStore.MediaColumns.SIZE,
        MediaStore.MediaColumns.VOLUME_NAME,
        MediaStore.MediaColumns.BUCKET_ID,
        MediaStore.MediaColumns.BUCKET_DISPLAY_NAME,
        MediaStore.Images.ImageColumns.DATE_TAKEN,
        MediaStore.MediaColumns.MIME_TYPE,
    )

    suspend fun query(context: Context, includeImages: Boolean = true, includeVideos: Boolean = true): List<DeviceMedia> = withContext(Dispatchers.IO) {
        val images = if (includeImages) queryCollection(
            context,
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            isVideo = false,
        ) else emptyList()
        val videos = if (includeVideos) queryCollection(
            context,
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI,
            isVideo = true,
        ) else emptyList()
        (images + videos).sortedByDescending { it.dateTakenMillis }
    }

    suspend fun forEachBatch(
        context: Context,
        batchSize: Int = 100,
        includeImages: Boolean = true,
        includeVideos: Boolean = true,
        onBatch: suspend (List<DeviceMedia>) -> Unit,
    ) = withContext(Dispatchers.IO) {
        require(batchSize > 0)
        if (includeImages) queryCollectionInBatches(
            context,
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            isVideo = false,
            batchSize = batchSize,
            onBatch = onBatch,
        )
        if (includeVideos) queryCollectionInBatches(
            context,
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI,
            isVideo = true,
            batchSize = batchSize,
            onBatch = onBatch,
        )
    }

    suspend fun findByKey(context: Context, mediaKey: String): DeviceMedia? = withContext(Dispatchers.IO) {
        val parts = mediaKey.split(':', limit = 2)
        val id = parts.getOrNull(1)?.toLongOrNull() ?: return@withContext null
        val isVideo = when (parts.firstOrNull()) {
            "image" -> false
            "video" -> true
            else -> return@withContext null
        }
        val collection = collection(isVideo)
        queryCollection(
            context,
            collection,
            isVideo,
            "${MediaStore.MediaColumns._ID} = ?",
            arrayOf(id.toString()),
        ).firstOrNull()
    }

    fun uriForKey(mediaKey: String): Uri? {
        val parts = mediaKey.split(':', limit = 2)
        val id = parts.getOrNull(1)?.toLongOrNull() ?: return null
        val isVideo = when (parts.firstOrNull()) {
            "image" -> false
            "video" -> true
            else -> return null
        }
        return ContentUris.withAppendedId(collection(isVideo), id)
    }

    private fun collection(isVideo: Boolean) = if (isVideo) {
        MediaStore.Video.Media.EXTERNAL_CONTENT_URI
    } else {
        MediaStore.Images.Media.EXTERNAL_CONTENT_URI
    }

    private fun queryCollection(
        context: Context,
        collection: Uri,
        isVideo: Boolean,
        selection: String? = null,
        selectionArgs: Array<String>? = null,
    ): List<DeviceMedia> {
        val result = ArrayList<DeviceMedia>()
        context.contentResolver.query(
            collection,
            projection,
            selection,
            selectionArgs,
            "${MediaStore.MediaColumns.DATE_ADDED} DESC",
        )?.use { cursor ->
            while (cursor.moveToNext()) result.add(readMedia(cursor, collection, isVideo))
        }
        return result
    }

    private suspend fun queryCollectionInBatches(
        context: Context,
        collection: Uri,
        isVideo: Boolean,
        batchSize: Int,
        onBatch: suspend (List<DeviceMedia>) -> Unit,
    ) {
        val batch = ArrayList<DeviceMedia>(batchSize)
        context.contentResolver.query(
            collection,
            projection,
            null,
            null,
            "${MediaStore.MediaColumns.DATE_ADDED} DESC",
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                currentCoroutineContext().ensureActive()
                batch.add(readMedia(cursor, collection, isVideo))
                if (batch.size == batchSize) {
                    onBatch(batch.toList())
                    batch.clear()
                }
            }
        }
        if (batch.isNotEmpty()) onBatch(batch)
    }

    private fun readMedia(cursor: android.database.Cursor, collection: Uri, isVideo: Boolean): DeviceMedia {
        val id = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns._ID))
        val dateAdded = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_ADDED))
        val dateTaken = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.Images.ImageColumns.DATE_TAKEN))
        return DeviceMedia(
            id = id,
            uri = ContentUris.withAppendedId(collection, id),
            isVideo = isVideo,
            name = cursor.getString(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DISPLAY_NAME)) ?: id.toString(),
            dateTakenMillis = dateTaken.takeIf { it > 0L } ?: dateAdded * 1000L,
            dateModifiedSeconds = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_MODIFIED)),
            sizeBytes = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.SIZE)),
            folderKey = "${cursor.getString(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.VOLUME_NAME))}:" +
                cursor.getString(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.BUCKET_ID)),
            folderName = cursor.getString(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.BUCKET_DISPLAY_NAME)).orEmpty(),
            mimeType = cursor.getString(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.MIME_TYPE)),
        )
    }
}
