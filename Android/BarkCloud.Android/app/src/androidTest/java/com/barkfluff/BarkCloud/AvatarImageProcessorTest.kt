package com.barkfluff.BarkCloud

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.media.ExifInterface
import androidx.core.content.FileProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.net.stageAvatarAndUpload
import com.barkfluff.BarkCloud.ui.settings.AvatarCrop
import com.barkfluff.BarkCloud.ui.settings.AvatarImageProcessor
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.File

@RunWith(AndroidJUnit4::class)
class AvatarImageProcessorTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun uri(file: File) = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)

    @Test fun croppedSquareIsJpeg90AndReachesUploadWithJpegMetadata() = runBlocking {
        val original = Bitmap.createBitmap(400, 200, Bitmap.Config.ARGB_8888)
        val expected = Bitmap.createBitmap(200, 200, Bitmap.Config.ARGB_8888)
        var output: File? = null
        try {
            Canvas(original).apply {
                drawColor(Color.RED)
                drawRect(200f, 0f, 400f, 200f, Paint().apply { color = Color.BLUE })
            }
            expected.eraseColor(Color.BLUE)
            output = AvatarImageProcessor.writeJpeg(context, original, AvatarCrop(panX = -0.5f))
            val bytes = ByteArrayOutputStream().also { expected.compress(Bitmap.CompressFormat.JPEG, 90, it) }.toByteArray()
            assertArrayEquals(bytes, output.readBytes())
            assertEquals("image/jpeg", context.contentResolver.getType(uri(output)))
            var staged: File? = null
            val result = stageAvatarAndUpload(context, uri(output)) { file, name, mime ->
                staged = file
                assertTrue(name.endsWith(".jpg"))
                assertEquals("image/jpeg", mime)
                assertArrayEquals(bytes, file.readBytes())
                "test-avatar-id"
            }
            assertEquals("test-avatar-id", result)
            assertFalse(staged!!.exists())
        } finally {
            output?.delete()
            original.recycle()
            expected.recycle()
        }
    }

    @Test fun transparentPixelsAreFlattenedOntoWhite() = runBlocking {
        val original = Bitmap.createBitmap(32, 32, Bitmap.Config.ARGB_8888)
        val output = AvatarImageProcessor.writeJpeg(context, original, AvatarCrop())
        try {
            val decoded = BitmapFactory.decodeFile(output.absolutePath)
            assertEquals(32, decoded.width)
            assertEquals(32, decoded.height)
            assertEquals(Color.WHITE, decoded.getPixel(16, 16))
            decoded.recycle()
        } finally {
            output.delete()
            original.recycle()
        }
    }

    @Test fun decodeRespectsExifRotationAndBoundsLargeImages() = runBlocking {
        val original = Bitmap.createBitmap(3000, 1500, Bitmap.Config.ARGB_8888)
        val input = File.createTempFile("avatar-orientation-", ".jpg", context.cacheDir)
        try {
            input.outputStream().use { original.compress(Bitmap.CompressFormat.JPEG, 90, it) }
            ExifInterface(input.absolutePath).apply {
                setAttribute(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_ROTATE_90.toString())
                saveAttributes()
            }
            val decoded = AvatarImageProcessor.decode(context, uri(input))
            assertEquals(1024, decoded.width)
            assertEquals(2048, decoded.height)
            decoded.recycle()
        } finally {
            input.delete()
            original.recycle()
        }
    }
}
