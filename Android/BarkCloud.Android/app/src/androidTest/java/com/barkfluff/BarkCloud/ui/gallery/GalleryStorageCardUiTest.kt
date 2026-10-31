package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.getBoundsInRoot
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.hasScrollAction
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeLeft
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.net.FileTransferService
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class GalleryStorageCardUiTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun loadingLoadedAndErrorKeepTheSameGeometryAtNormalAndLargeText() {
        val loaded = FileTransferService.StorageInfo(
            used = 512,
            limit = 1024,
            byType = mapOf(
                UploadFileType.CLOUD_FILE to 512,
                UploadFileType.USER_AVATAR to 0,
            ),
        )
        val cases = listOf(
            StorageCase(storage = null, loading = true),
            StorageCase(storage = loaded, loading = false),
            StorageCase(storage = null, loading = false),
        )
        val selectedCase = mutableStateOf(cases.first())
        val fontScale = mutableFloatStateOf(1f)
        compose.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, fontScale.floatValue)) {
                Box(Modifier.width(260.dp)) {
                    GalleryStorageCard(selectedCase.value.storage, selectedCase.value.loading)
                }
            }
        }

        for (scale in listOf(1f, 2f)) {
            compose.runOnIdle { fontScale.floatValue = scale }
            val heights = cases.map { caseState ->
                compose.runOnIdle { selectedCase.value = caseState }
                compose.waitForIdle()
                val bounds = compose.onNodeWithTag("gallery-storage-card").getBoundsInRoot()
                (bounds.bottom - bounds.top).value
            }
            assertEquals(heights[0], heights[1], 1f)
            assertEquals(heights[1], heights[2], 1f)
        }

        compose.runOnIdle { selectedCase.value = StorageCase(storage = loaded, loading = false) }
        compose.onNodeWithText("Прочее").fetchSemanticsNode()
        val categories = compose.onNodeWithTag("gallery-storage-categories")
        categories.assert(hasScrollAction())
        val filesLeftBeforeScroll = compose.onNodeWithText("Файлы").getUnclippedBoundsInRoot().left.value
        categories.performTouchInput { swipeLeft() }
        compose.waitForIdle()
        val filesLeftAfterScroll = compose.onNodeWithText("Файлы").getUnclippedBoundsInRoot().left.value
        assertTrue(filesLeftAfterScroll < filesLeftBeforeScroll)
    }

    private data class StorageCase(val storage: FileTransferService.StorageInfo?, val loading: Boolean)
}
