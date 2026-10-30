package com.barkfluff.BarkCloud.ui.components

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeDown
import androidx.compose.ui.test.swipeUp
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicBoolean

class CloudMediaViewerGestureTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun propertyScrollCollapsesWithoutDismissingAndPhotoSwipeDismissesFromCollapsed() {
        val dismissed = AtomicBoolean(false)
        val infoExpanded = AtomicBoolean(false)
        compose.setContent {
            CloudMediaViewerLayer(
                infoEnabled = true,
                isOpen = true,
                isZoomed = false,
                onDismiss = { dismissed.set(true) },
                onPanelExpanded = infoExpanded::set,
                photoContent = { _, _ ->
                    Box(Modifier.fillMaxSize().testTag("viewer-photo"))
                },
                overlayContent = { _, _ -> },
                infoContent = {
                    Column(
                        Modifier
                            .fillMaxSize()
                            .verticalScroll(rememberScrollState())
                            .testTag("viewer-properties"),
                    ) {
                        repeat(40) { index ->
                            Text("Property $index", Modifier.height(40.dp))
                        }
                    }
                },
            )
        }

        compose.onNodeWithTag("viewer-photo").performTouchInput { swipeUp() }
        compose.waitUntil(3_000) { infoExpanded.get() }
        compose.onNodeWithTag("viewer-properties").assertIsDisplayed()

        compose.onNodeWithTag("viewer-properties").performTouchInput { swipeDown() }
        compose.waitUntil(3_000) { !infoExpanded.get() }
        assertFalse(dismissed.get())

        compose.onNodeWithTag("viewer-photo").performTouchInput { swipeDown() }
        compose.waitUntil(3_000) { dismissed.get() }
        assertTrue(dismissed.get())
    }
}
