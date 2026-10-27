package com.barkfluff.BarkCloud.ui.settings

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ImageDecoder
import android.graphics.Paint
import android.graphics.Rect
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import kotlin.math.roundToInt

internal object AvatarImageProcessor {
    suspend fun decode(context: Context, uri: Uri): Bitmap = withContext(Dispatchers.IO) {
        ImageDecoder.decodeBitmap(ImageDecoder.createSource(context.contentResolver, uri)) { decoder, info, _ ->
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            val ratio = (2048f / maxOf(info.size.width, info.size.height)).coerceAtMost(1f)
            decoder.setTargetSize(
                (info.size.width * ratio).roundToInt().coerceAtLeast(1),
                (info.size.height * ratio).roundToInt().coerceAtLeast(1),
            )
        }
    }

    suspend fun writeJpeg(context: Context, bitmap: Bitmap, crop: AvatarCrop): File {
        var file: File? = null
        try {
            return withContext(Dispatchers.IO) {
                val bounds = crop.pixels(bitmap.width, bitmap.height)
                val side = bounds.side.coerceAtMost(1024)
                val output = Bitmap.createBitmap(side, side, Bitmap.Config.ARGB_8888)
                try {
                    Canvas(output).apply {
                        drawColor(Color.WHITE)
                        drawBitmap(
                            bitmap,
                            Rect(bounds.left, bounds.top, bounds.left + bounds.side, bounds.top + bounds.side),
                            Rect(0, 0, side, side),
                            Paint(Paint.FILTER_BITMAP_FLAG),
                        )
                    }
                    val target = File.createTempFile("avatar-crop-", ".jpg", context.cacheDir)
                    file = target
                    target.outputStream().use { stream ->
                        if (!output.compress(Bitmap.CompressFormat.JPEG, 90, stream)) {
                            throw IOException("Не удалось сохранить аватар")
                        }
                    }
                    target
                } finally {
                    output.recycle()
                }
            }
        } catch (e: Exception) {
            file?.delete()
            throw e
        }
    }
}
