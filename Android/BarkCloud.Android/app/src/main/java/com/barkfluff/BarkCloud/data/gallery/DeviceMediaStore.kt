package com.barkfluff.BarkCloud.data.gallery

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import kotlinx.coroutines.Dispatchers
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
) {
    val mediaKey: String get() = "${if (isVideo) "video" else "image"}:$id"
}

/** Чтение медиатеки устройства через MediaStore (фото + видео, от новых к старым). */
object DeviceMediaStore {

    suspend fun query(context: Context): List<DeviceMedia> = withContext(Dispatchers.IO) {
        val images = queryCollection(
            context,
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            isVideo = false,
        )
        val videos = queryCollection(
            context,
            MediaStore.Video.Media.EXTERNAL_CONTENT_URI,
            isVideo = true,
        )
        (images + videos).sortedByDescending { it.dateTakenMillis }
    }

    private fun queryCollection(
        context: Context,
        collection: Uri,
        isVideo: Boolean,
    ): List<DeviceMedia> {
        val projection = arrayOf(
            MediaStore.MediaColumns._ID,
            MediaStore.MediaColumns.DISPLAY_NAME,
            MediaStore.MediaColumns.DATE_ADDED,
            MediaStore.MediaColumns.DATE_MODIFIED,
            MediaStore.MediaColumns.SIZE,
            MediaStore.Images.ImageColumns.DATE_TAKEN,
        )
        val result = ArrayList<DeviceMedia>()
        context.contentResolver.query(
            collection,
            projection,
            null,
            null,
            "${MediaStore.MediaColumns.DATE_ADDED} DESC",
        )?.use { cursor ->
            val idCol = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns._ID)
            val nameCol = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DISPLAY_NAME)
            val dateCol = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_ADDED)
            val dateTakenCol = cursor.getColumnIndexOrThrow(MediaStore.Images.ImageColumns.DATE_TAKEN)
            val modifiedCol = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.DATE_MODIFIED)
            val sizeCol = cursor.getColumnIndexOrThrow(MediaStore.MediaColumns.SIZE)
            while (cursor.moveToNext()) {
                val id = cursor.getLong(idCol)
                result.add(
                    DeviceMedia(
                        id = id,
                        uri = ContentUris.withAppendedId(collection, id),
                        isVideo = isVideo,
                        name = cursor.getString(nameCol) ?: id.toString(),
                        dateTakenMillis = cursor.getLong(dateTakenCol)
                            .takeIf { it > 0L }
                            ?: cursor.getLong(dateCol) * 1000L,
                        dateModifiedSeconds = cursor.getLong(modifiedCol),
                        sizeBytes = cursor.getLong(sizeCol),
                    )
                )
            }
        }
        return result
    }
}
