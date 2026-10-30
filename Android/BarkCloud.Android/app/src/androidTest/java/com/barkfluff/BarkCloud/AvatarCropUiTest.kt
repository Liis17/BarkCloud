package com.barkfluff.BarkCloud

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import androidx.activity.ComponentActivity
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.core.content.FileProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.barkfluff.BarkCloud.ui.settings.AvatarCropDialog
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class AvatarCropUiTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test fun draggingCropThenApplyingExportsTheVisibleRegion() {
        val context = compose.activity
        val original = Bitmap.createBitmap(400, 200, Bitmap.Config.ARGB_8888)
        val expected = Bitmap.createBitmap(200, 200, Bitmap.Config.ARGB_8888)
        val input = File.createTempFile("avatar-ui-", ".png", context.cacheDir)
        val output = AtomicReference<File?>()
        try {
            Canvas(original).apply {
                drawColor(Color.RED)
                drawRect(200f, 0f, 400f, 200f, Paint().apply { color = Color.BLUE })
            }
            expected.eraseColor(Color.BLUE)
            input.outputStream().use { original.compress(Bitmap.CompressFormat.PNG, 100, it) }
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", input)
            compose.setContent {
                BarkCloudTheme { AvatarCropDialog(uri, onDismiss = {}, onConfirm = { output.set(it) }) }
            }
            compose.waitUntil(10_000) {
                compose.onAllNodesWithContentDescription("Область обрезки аватара").fetchSemanticsNodes().isNotEmpty()
            }
            compose.onNodeWithContentDescription("Область обрезки аватара").performTouchInput {
                swipe(Offset(width * 0.9f, height * 0.5f), Offset(width * 0.1f, height * 0.5f))
            }
            compose.onNodeWithText("Применить").performClick()
            compose.waitUntil(10_000) { output.get() != null }
            val reference = ByteArrayOutputStream().also { expected.compress(Bitmap.CompressFormat.JPEG, 90, it) }.toByteArray()
            assertArrayEquals(reference, output.get()!!.readBytes())
        } finally {
            output.get()?.delete()
            input.delete()
            original.recycle()
            expected.recycle()
        }
    }
}
