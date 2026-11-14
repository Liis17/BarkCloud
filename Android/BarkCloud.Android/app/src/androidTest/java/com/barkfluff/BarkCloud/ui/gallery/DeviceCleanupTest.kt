package com.barkfluff.BarkCloud.ui.gallery

import android.net.Uri
import android.Manifest
import android.os.Build
import androidx.activity.ComponentActivity
import androidx.compose.animation.core.Animatable
import androidx.compose.runtime.remember
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

    @Test fun waveAnimationFinishesWithoutDeletingFiles() {
        val running = androidx.compose.runtime.mutableStateOf(false)
        var finished = false
        compose.mainClock.autoAdvance = false
        compose.setContent {
            BarkCloudTheme {
                val elapsed = remember { Animatable(0f) }
                CleanupWaveGrid((1L..9L).map { media(it, 100) }, running.value, elapsed, { finished = true })
            }
        }
        compose.waitForIdle()
        compose.runOnIdle { running.value = true }
        compose.mainClock.advanceTimeBy(2000)
        compose.waitUntil(5000) { finished }
    }

    @Test fun accountButtonOpensCleanupScreenAndCloseReturnsToPhotos() {
        val permissions = if (Build.VERSION.SDK_INT >= 33) {
            listOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        } else listOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        permissions.forEach {
            InstrumentationRegistry.getInstrumentation().uiAutomation.grantRuntimePermission(compose.activity.packageName, it)
        }
        compose.setContent { BarkCloudTheme { MainScreen(onSignOut = {}) } }
        compose.onNodeWithContentDescription("Меню аккаунта").performClick()
        compose.onNodeWithText("Освободить место").performScrollTo().performClick()
        compose.onNodeWithText("Освобождаем место").assertIsDisplayed()
        compose.onNodeWithContentDescription("Закрыть").performClick()
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
        compose.setContent {
            BarkCloudTheme { DeviceCleanupContent(state, CleanupPhase.Idle, onClean = { cleanupCalls++ }, onClose = {}) }
        }
        compose.runOnIdle { assertEquals(0, cleanupCalls) }
        compose.onNodeWithText("Освободить место").assertIsEnabled().performClick()
        compose.runOnIdle { assertEquals(1, cleanupCalls) }
    }

    @Test fun noCopiesDisablesDestructiveAction() {
        compose.setContent {
            BarkCloudTheme { DeviceCleanupContent(GalleryUiState(), CleanupPhase.Idle, onClean = {}, onClose = {}) }
        }
        compose.onNodeWithText("Освободить место").assertIsNotEnabled()
    }

    @Test fun checkingCopiesDisablesRepeatedCleanup() {
        val item = media(1, 256L * 1024 * 1024)
        val state = GalleryUiState(items = listOf(item), cloudStates = mapOf(item.mediaKey to MediaCloudStatus.IN_CLOUD))
        compose.setContent {
            BarkCloudTheme { DeviceCleanupContent(state, CleanupPhase.Checking, onClean = {}, onClose = {}) }
        }
        compose.onNodeWithText("Освобождаем…").assertIsNotEnabled()
    }

    @Test fun doneShowsFreedSpaceAndCloseLeavesScreen() {
        var closeCalls = 0
        compose.setContent {
            BarkCloudTheme {
                DeviceCleanupContent(
                    GalleryUiState(), CleanupPhase.Done, onClean = {}, onClose = { closeCalls++ },
                    freedBytes = 256L * 1024 * 1024,
                )
            }
        }
        compose.onNodeWithText("Готово!").assertIsDisplayed()
        compose.onNodeWithText("освобождено", substring = true).assertIsDisplayed()
        compose.onNodeWithText("Закрыть").performClick()
        compose.runOnIdle { assertEquals(1, closeCalls) }
    }

    private fun media(id: Long, size: Long) = DeviceMedia(id, Uri.EMPTY, false, "$id.jpg", 0, 0, size)
}
