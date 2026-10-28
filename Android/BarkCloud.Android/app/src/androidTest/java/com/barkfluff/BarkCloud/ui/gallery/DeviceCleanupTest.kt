package com.barkfluff.BarkCloud.ui.gallery

import android.net.Uri
import android.Manifest
import android.os.Build
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import com.barkfluff.BarkCloud.ui.main.MainScreen
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class DeviceCleanupTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test fun accountButtonOpensCleanupScreenAndBackReturnsToPhotos() {
        val permissions = if (Build.VERSION.SDK_INT >= 33) {
            listOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        } else listOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        permissions.forEach {
            InstrumentationRegistry.getInstrumentation().uiAutomation.grantRuntimePermission(compose.activity.packageName, it)
        }
        compose.setContent { BarkCloudTheme { MainScreen(onSignOut = {}) } }
        compose.onNodeWithContentDescription("Меню аккаунта").performClick()
        compose.onNodeWithText("Освободить место").performScrollTo().performClick()
        compose.onNodeWithText("Можно освободить", substring = true).assertIsDisplayed()
        compose.onNodeWithContentDescription("Назад").performClick()
        compose.onNodeWithContentDescription("Меню аккаунта").assertIsDisplayed()
    }

    @Test fun countsOnlyCloudCopiesAndWaitsForExplicitCleanupClick() {
        val uploaded = media(1, 256L * 1024 * 1024)
        val local = media(2, 1024L * 1024 * 1024)
        val state = GalleryUiState(
            items = listOf(uploaded, local),
            cloudStates = mapOf(uploaded.mediaKey to MediaCloudStatus.IN_CLOUD, local.mediaKey to MediaCloudStatus.NOT_IN_CLOUD),
        )
        assertEquals(uploaded.sizeBytes, state.reclaimableBytes)
        var cleanupCalls = 0
        compose.setContent { BarkCloudTheme { DeviceCleanupContent(state, busy = false) { cleanupCalls++ } } }
        compose.runOnIdle { assertEquals(0, cleanupCalls) }
        compose.onNodeWithText("Удалить локальные копии", substring = true).assertIsEnabled().performClick()
        compose.runOnIdle { assertEquals(1, cleanupCalls) }
    }

    @Test fun noCopiesDisablesDestructiveAction() {
        compose.setContent { BarkCloudTheme { DeviceCleanupContent(GalleryUiState(), busy = false) {} } }
        compose.onNodeWithText("Удалить локальные копии", substring = true).assertIsNotEnabled()
    }

    @Test fun checkingCopiesDisablesRepeatedCleanup() {
        val item = media(1, 256L * 1024 * 1024)
        val state = GalleryUiState(items = listOf(item), cloudStates = mapOf(item.mediaKey to MediaCloudStatus.IN_CLOUD))
        compose.setContent { BarkCloudTheme { DeviceCleanupContent(state, busy = true) {} } }
        compose.onNodeWithText("Удалить локальные копии", substring = true).assertIsNotEnabled()
    }

    private fun media(id: Long, size: Long) = DeviceMedia(id, Uri.EMPTY, false, "$id.jpg", 0, 0, size)
}
