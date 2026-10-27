package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

class GalleryLayoutUiTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun roundedSurfaceCollapsesToStatusBarAndTracksPullWhileReachingBottom() {
        val topOffsetRef = AtomicReference<androidx.compose.runtime.MutableFloatState>()
        val densityRef = AtomicReference<androidx.compose.ui.unit.Density>()
        compose.setContent {
            val density = LocalDensity.current
            val topOffset = remember { mutableFloatStateOf(with(density) { 100.dp.toPx() }) }
            SideEffect {
                topOffsetRef.set(topOffset)
                densityRef.set(density)
            }
            Box(Modifier.size(width = 300.dp, height = 600.dp)) {
                GalleryContentSurface(
                    modifier = Modifier.fillMaxSize(),
                    topOffsetPx = { topOffset.floatValue },
                ) {
                    Box(Modifier.fillMaxSize().testTag("gallery-surface-content"))
                }
            }
        }
        compose.waitForIdle()

        assertSurfaceBounds(top = 100.dp, bottom = 600.dp)
        compose.runOnIdle {
            val density = densityRef.get()
            topOffsetRef.get().floatValue = with(density) {
                gallerySurfaceTopPx(100.dp.toPx(), 64.dp.toPx(), 0f)
            }
        }
        assertSurfaceBounds(top = 36.dp, bottom = 600.dp)

        compose.runOnIdle {
            val density = densityRef.get()
            topOffsetRef.get().floatValue = with(density) {
                gallerySurfaceTopPx(100.dp.toPx(), 64.dp.toPx(), 20.dp.toPx())
            }
        }
        assertSurfaceBounds(top = 56.dp, bottom = 600.dp)
    }

    @Test
    fun selectionCountAndCloseStayVisibleAfterGridScrolls() {
        val closeClicked = AtomicBoolean(false)
        compose.setContent {
            val gridState = rememberLazyGridState()
            Box(Modifier.size(width = 360.dp, height = 640.dp)) {
                LazyVerticalGrid(
                    columns = GridCells.Fixed(2),
                    state = gridState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(bottom = 150.dp),
                ) {
                    items((0..40).toList()) { index ->
                        Box(Modifier.height(80.dp).testTag("gallery-item-$index"))
                    }
                }
                GallerySelectionToolbar(
                    modifier = Modifier.align(Alignment.BottomCenter),
                    actions = PhotoSelectionActions(deviceCount = 1, cloudCount = 1),
                    isBusy = false,
                    isQueueing = false,
                    onUpload = {},
                    onFavorite = {},
                    onDelete = {},
                )
                GalleryHeader(
                    modifier = Modifier.align(Alignment.TopCenter),
                    height = 64.dp,
                    avatarUrl = null,
                    onAccount = {},
                    selecting = true,
                    selectedCount = 2,
                    onClearSelection = { closeClicked.set(true) },
                )
                LaunchedEffect(Unit) { gridState.scrollToItem(30) }
            }
        }
        compose.waitForIdle()

        compose.onNodeWithTag("gallery-item-30").assertIsDisplayed()
        compose.onNodeWithText("Выбрано: 2").assertIsDisplayed()
        compose.onNodeWithContentDescription("Отмена").assertIsDisplayed().performClick()
        compose.runOnIdle { assertTrue(closeClicked.get()) }
    }

    private fun assertSurfaceBounds(top: androidx.compose.ui.unit.Dp, bottom: androidx.compose.ui.unit.Dp) {
        val bounds = compose.onNodeWithTag("gallery-surface-content").getUnclippedBoundsInRoot()
        assertEquals(top.value, bounds.top.value, 1f)
        assertEquals(bottom.value, bounds.bottom.value, 1f)
    }
}
