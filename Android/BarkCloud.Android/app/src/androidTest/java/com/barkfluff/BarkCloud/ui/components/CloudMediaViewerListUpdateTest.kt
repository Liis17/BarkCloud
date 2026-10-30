package com.barkfluff.BarkCloud.ui.components

import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicBoolean

class CloudMediaViewerListUpdateTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun removingCurrentAssetKeepsViewerOnRemainingAssetWithoutDismissing() {
        val assetA = photo("asset-a")
        val assetB = photo("asset-b")
        val assets = mutableStateOf(listOf(assetA, assetB))
        val dismissed = AtomicBoolean(false)

        compose.setContent {
            CloudMediaTransitionLayout("asset-b") {
                CloudMediaViewer(
                    assets = assets.value,
                    selectedAsset = assetB,
                    onDismiss = { dismissed.set(true) },
                    onPlayVideo = {},
                    infoEnabled = false,
                )
            }
        }

        compose.onNodeWithContentDescription("asset-b.jpg").assertIsDisplayed()
        compose.runOnIdle { assets.value = listOf(assetA) }

        compose.onNodeWithContentDescription("asset-a.jpg").assertIsDisplayed()
        compose.onNodeWithContentDescription("asset-b.jpg").assertDoesNotExist()
        assertFalse(dismissed.get())
    }

    private fun photo(id: String) = MediaAsset(
        id = id,
        fileName = "$id.jpg",
        fileSize = 1L,
        kind = CloudMediaKind.PHOTO,
        previews = emptyList(),
        createdAtMillis = 0L,
    )
}
