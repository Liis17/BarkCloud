package com.barkfluff.BarkCloud.data.cache

import android.content.Context
import android.graphics.Bitmap
import android.media.ThumbnailUtils
import android.net.Uri
import android.os.CancellationSignal
import android.util.Size
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException

class AndroidLocalMediaThumbnailLoader(
    private val context: Context,
) : LocalMediaThumbnailLoader {
    override suspend fun load(model: LocalMediaImageModel, destination: File) = withContext(Dispatchers.IO) {
        val size = Size(model.maxDimensionPx, model.maxDimensionPx)
        val signal = CancellationSignal()
        val uri = Uri.parse(model.uri)
        val thumbnail = when (uri.scheme) {
            "content" -> context.contentResolver.loadThumbnail(uri, size, signal)
            "file" -> {
                val file = File(requireNotNull(uri.path))
                if (model.isVideo) ThumbnailUtils.createVideoThumbnail(file, size, signal)
                else ThumbnailUtils.createImageThumbnail(file, size, signal)
            }
            else -> throw IOException("Неподдерживаемый источник локального превью")
        }

        thumbnail.useBitmap { bitmap ->
            destination.outputStream().use { output ->
                if (!bitmap.compress(Bitmap.CompressFormat.WEBP_LOSSY, 85, output)) {
                    throw IOException("Не удалось записать локальное превью")
                }
            }
        }
    }
}

private inline fun <T> Bitmap.useBitmap(block: (Bitmap) -> T): T = try {
    block(this)
} finally {
    recycle()
}
