package com.barkfluff.BarkCloud.ui.gallery

import com.barkfluff.BarkCloud.ui.components.RefreshContentSurface
import com.barkfluff.BarkCloud.ui.components.RefreshIndicator
import com.barkfluff.BarkCloud.ui.components.contentSurfaceTopPx
import com.barkfluff.BarkCloud.ui.components.RefreshableContentSurface

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.getBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeDown
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred

class GalleryLayoutUiTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun roundedSurfaceCollapsesToStatusBarAndTracksPullWhileReachingBottom() {
        val topOffsetRef = AtomicReference<androidx.compose.runtime.MutableFloatState>()
        val pullOffset = mutableFloatStateOf(0f)
        val densityRef = AtomicReference<androidx.compose.ui.unit.Density>()
        compose.setContent {
            val density = LocalDensity.current
            val topOffset = remember { mutableFloatStateOf(with(density) { 100.dp.toPx() }) }
            SideEffect {
                topOffsetRef.set(topOffset)
                densityRef.set(density)
            }
            Box(Modifier.size(width = 300.dp, height = 600.dp)) {
                RefreshContentSurface(
                    modifier = Modifier.fillMaxSize(),
                    topOffsetPx = { topOffset.floatValue },
                    pullOffsetPx = { with(density) { pullOffset.floatValue.dp.toPx() } },
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
                contentSurfaceTopPx(100.dp.toPx(), 64.dp.toPx())
            }
        }
        assertSurfaceBounds(top = 36.dp, bottom = 600.dp)

        compose.runOnIdle {
            pullOffset.floatValue = 20f
        }
        assertSurfaceBounds(top = 56.dp, bottom = 600.dp)
    }

    @Test
    fun pullingMovesPhotosWithoutResizingTheGridViewport() {
        val pullOffset = mutableFloatStateOf(0f)
        compose.setContent {
            val density = LocalDensity.current
            Box(Modifier.size(width = 300.dp, height = 600.dp)) {
                RefreshContentSurface(
                    topOffsetPx = { with(density) { 100.dp.toPx() } },
                    pullOffsetPx = { with(density) { pullOffset.floatValue.dp.toPx() } },
                ) {
                    LazyVerticalGrid(
                        columns = GridCells.Fixed(3),
                        modifier = Modifier.fillMaxSize().testTag("moving-grid"),
                    ) {
                        items((0..50).toList(), key = { it }) { index ->
                            Box(Modifier.height(100.dp).testTag("moving-photo-$index"))
                        }
                    }
                }
            }
        }
        val initialGrid = compose.onNodeWithTag("moving-grid").getUnclippedBoundsInRoot()
        val initialPhoto = compose.onNodeWithTag("moving-photo-0").getUnclippedBoundsInRoot()

        compose.runOnIdle { pullOffset.floatValue = 40f }

        val pulledGrid = compose.onNodeWithTag("moving-grid").getUnclippedBoundsInRoot()
        val pulledPhoto = compose.onNodeWithTag("moving-photo-0").getUnclippedBoundsInRoot()
        assertEquals(initialPhoto.top.value + 40f, pulledPhoto.top.value, 1f)
        assertEquals(
            (initialGrid.bottom - initialGrid.top).value,
            (pulledGrid.bottom - pulledGrid.top).value,
            1f,
        )

        compose.runOnIdle { pullOffset.floatValue = 0f }
        val restoredPhoto = compose.onNodeWithTag("moving-photo-0").getUnclippedBoundsInRoot()
        assertEquals(initialPhoto.top.value, restoredPhoto.top.value, 1f)
    }

    @Test
    fun refreshIndicatorStaysCenteredBetweenStatusBarAndPhotoSurface() {
        val gapHeight = mutableFloatStateOf(100f)
        val refreshing = mutableStateOf(false)
        compose.setContent {
            val density = LocalDensity.current
            Box(Modifier.size(width = 300.dp, height = 600.dp)) {
                RefreshIndicator(
                    pullProgress = { 1f },
                    isRefreshing = refreshing.value,
                    gapHeightPx = { with(density) { gapHeight.floatValue.dp.toPx() } },
                    modifier = Modifier.padding(top = 24.dp),
                )
            }
        }
        fun assertEqualGaps() {
            val bounds = compose.onNode(
                SemanticsMatcher.keyIsDefined(SemanticsProperties.ProgressBarRangeInfo),
            ).getUnclippedBoundsInRoot()
            assertEquals(bounds.top.value - 24f, 24f + gapHeight.floatValue - bounds.bottom.value, 1f)
        }
        assertEqualGaps()
        compose.runOnIdle { gapHeight.floatValue = 180f }
        assertEqualGaps()
        compose.runOnIdle { refreshing.value = true }
        assertEqualGaps()
        compose.runOnIdle { gapHeight.floatValue = 80f }
        assertEqualGaps()
    }

    @Test
    fun rootSurfaceCentersIndicatorAndWaitsForRefreshWithoutOverlappingRequests() {
        val request = CompletableDeferred<Unit>()
        val requestCount = AtomicInteger(0)
        compose.setContent {
            Box(Modifier.size(width = 300.dp, height = 600.dp)) {
                RefreshableContentSurface(
                    onRefresh = { requestCount.incrementAndGet(); request.await() },
                    header = { Box(Modifier.height(64.dp)) },
                    modifier = Modifier.testTag("refresh-root"),
                ) {
                    LazyColumn(Modifier.fillMaxSize().testTag("refresh-list")) {
                        items(30) { Box(Modifier.height(80.dp)) }
                    }
                }
            }
        }
        compose.onNodeWithTag("refresh-root").performTouchInput {
            swipeDown(startY = height * 0.25f, endY = height * 0.9f)
        }
        compose.waitUntil { requestCount.get() == 1 }
        val indicator = compose.onNode(SemanticsMatcher.keyIsDefined(SemanticsProperties.ProgressBarRangeInfo))
        val bounds = indicator.getUnclippedBoundsInRoot()
        val surfaceTop = compose.onNodeWithTag("refresh-list").getUnclippedBoundsInRoot().top.value
        assertEquals(surfaceTop / 2f, (bounds.top.value + bounds.bottom.value) / 2f, 1f)

        compose.onNodeWithTag("refresh-root").performTouchInput {
            swipeDown(startY = height * 0.25f, endY = height * 0.9f)
        }
        compose.runOnIdle { assertEquals(1, requestCount.get()) }
        indicator.assertIsDisplayed()
        compose.runOnIdle { request.complete(Unit) }
        compose.waitUntil(timeoutMillis = 5_000) {
            compose.onAllNodes(SemanticsMatcher.keyIsDefined(SemanticsProperties.ProgressBarRangeInfo))
                .fetchSemanticsNodes().isEmpty()
        }
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
        val bounds = compose.onNodeWithTag("gallery-surface-content").getBoundsInRoot()
        assertEquals(top.value, bounds.top.value, 1f)
        assertEquals(bottom.value, bounds.bottom.value, 1f)
    }
}
