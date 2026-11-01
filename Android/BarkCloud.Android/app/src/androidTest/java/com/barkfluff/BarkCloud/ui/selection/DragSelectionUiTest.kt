package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

/** Pointer-input integration tests; run on a device or emulator. */
class DragSelectionUiTest {

    @get:Rule
    val rule = createComposeRule()

    @Test
    fun longPressAndDragSelectsInclusiveRangeInDisplayOrder() {
        val keys = (0 until 30).map { "k$it" }
        var selection by mutableStateOf(emptySet<String>())
        var dragging by mutableStateOf(false)
        rule.mainClock.autoAdvance = false
        rule.setContent {
            SelectionGrid(
                keys = keys,
                selection = selection,
                onSelectionChange = { selection = it },
                onDragActiveChange = { dragging = it },
                onGridState = {},
            )
        }
        rule.waitForIdle()

        val cell = with(rule.density) { 100.dp.toPx() }
        val from = Offset(1.5f * cell, 2.5f * cell)
        val to = Offset(1.5f * cell, 3.5f * cell)
        beginLongPress(from) { dragging }
        rule.onNodeWithTag("grid").performTouchInput {
            for (step in 1..10) {
                advanceEventTime(16)
                moveTo(from + (to - from) * (step / 10f))
            }
            up()
        }
        rule.mainClock.advanceTimeUntil(1_000L) { !dragging }

        assertEquals(setOf("k7", "k8", "k9", "k10"), selection)
    }

    @Test
    fun reversingDragShrinksRangeAndRetainsSelectionOutsideIt() {
        val keys = (0 until 30).map { "k$it" }
        var selection by mutableStateOf(setOf("k20"))
        var dragging by mutableStateOf(false)
        rule.mainClock.autoAdvance = false
        rule.setContent {
            SelectionGrid(
                keys = keys,
                selection = selection,
                onSelectionChange = { selection = it },
                onDragActiveChange = { dragging = it },
                onGridState = {},
            )
        }
        rule.waitForIdle()

        val cell = with(rule.density) { 100.dp.toPx() }
        val from = Offset(1.5f * cell, 2.5f * cell)
        val far = Offset(1.5f * cell, 3.5f * cell)
        val back = Offset(2.5f * cell, 2.5f * cell)
        beginLongPress(from) { dragging }
        rule.onNodeWithTag("grid").performTouchInput {
            moveTo(far)
            advanceEventTime(16)
            moveTo(back)
            up()
        }
        rule.mainClock.advanceTimeUntil(1_000L) { !dragging }

        assertEquals(setOf("k7", "k8", "k20"), selection)
    }

    @Test
    fun stationaryFingerAtTopEdgeAutoscrollsAndExtendsRange() {
        val keys = (0 until 60).map { "k$it" }
        var selection by mutableStateOf(emptySet<String>())
        var dragging by mutableStateOf(false)
        var gridState: LazyGridState? = null
        rule.mainClock.autoAdvance = false
        rule.setContent {
            SelectionGrid(
                keys = keys,
                selection = selection,
                onSelectionChange = { selection = it },
                initialFirstVisibleItemIndex = 15,
                onDragActiveChange = { dragging = it },
                onGridState = { gridState = it },
            )
        }
        rule.waitForIdle()

        val grid = requireNotNull(gridState)
        val firstBefore = grid.firstVisibleItemIndex
        val cell = with(rule.density) { 100.dp.toPx() }
        val from = Offset(0.5f * cell, 3.5f * cell) // k24, away from either edge
        beginLongPress(from) { dragging }
        rule.onNodeWithTag("grid").performTouchInput {
            moveTo(Offset(0.5f * cell, 0.1f * cell))
        }
        val sizeAtEdge = selection.size
        // The autoscroll delay uses the live coroutine scope, so wait for the observed state change.
        rule.waitUntil(5_000L) {
            rule.mainClock.advanceTimeByFrame()
            grid.firstVisibleItemIndex < firstBefore && selection.size > sizeAtEdge
        }
        rule.onNodeWithTag("grid").performTouchInput { up() }
        rule.mainClock.advanceTimeByFrame()

        assertTrue("the grid should scroll upward under a stationary finger", grid.firstVisibleItemIndex < firstBefore)
        assertTrue("the range should extend to the newly visible top row", selection.any { it.drop(1).toInt() < firstBefore })
    }

    @Test
    fun stationaryFingerAtBottomEdgeAutoscrollsAndExtendsRange() {
        val keys = (0 until 60).map { "k$it" }
        var selection by mutableStateOf(emptySet<String>())
        var dragging by mutableStateOf(false)
        var gridState: LazyGridState? = null
        rule.mainClock.autoAdvance = false
        rule.setContent {
            SelectionGrid(
                keys = keys,
                selection = selection,
                onSelectionChange = { selection = it },
                initialFirstVisibleItemIndex = 15,
                onDragActiveChange = { dragging = it },
                onGridState = { gridState = it },
            )
        }
        rule.waitForIdle()

        val grid = requireNotNull(gridState)
        val firstBefore = grid.firstVisibleItemIndex
        val lastBefore = grid.layoutInfo.visibleItemsInfo.maxOf { it.index }
        val cell = with(rule.density) { 100.dp.toPx() }
        val from = Offset(0.5f * cell, 3.5f * cell) // k24, away from either edge
        beginLongPress(from) { dragging }
        rule.onNodeWithTag("grid").performTouchInput {
            moveTo(Offset(0.5f * cell, 5.9f * cell))
        }
        val sizeAtEdge = selection.size
        rule.waitUntil(5_000L) {
            rule.mainClock.advanceTimeByFrame()
            grid.firstVisibleItemIndex > firstBefore && selection.size > sizeAtEdge
        }
        rule.onNodeWithTag("grid").performTouchInput { up() }
        rule.mainClock.advanceTimeByFrame()

        assertTrue("the grid should scroll downward under a stationary finger", grid.firstVisibleItemIndex > firstBefore)
        assertTrue("the range should extend to the newly visible bottom row", selection.any { it.drop(1).toInt() > lastBefore })
    }

    private fun beginLongPress(position: Offset, dragging: () -> Boolean) {
        rule.onNodeWithTag("grid").performTouchInput {
            down(position)
            advanceEventTime(600)
        }
        rule.mainClock.advanceTimeUntil(1_000L, dragging)
    }
}

@Composable
private fun SelectionGrid(
    keys: List<String>,
    selection: Set<String>,
    onSelectionChange: (Set<String>) -> Unit,
    initialFirstVisibleItemIndex: Int = 0,
    onDragActiveChange: (Boolean) -> Unit,
    onGridState: (LazyGridState) -> Unit,
) {
    val grid = rememberLazyGridState(initialFirstVisibleItemIndex = initialFirstVisibleItemIndex)
    var dragging by remember { mutableStateOf(false) }
    SideEffect { onGridState(grid) }
    val controller = rememberDragSelection(
        orderedKeys = keys,
        selected = selection,
        onSelectionChange = onSelectionChange,
        visibleCells = { grid.visibleCells() },
        viewport = { grid.viewportBounds(bottomInsetPx = 0f) },
        scrollBy = { grid.scrollBy(it) },
        onDragActiveChange = {
            dragging = it
            onDragActiveChange(it)
        },
    )
    LazyVerticalGrid(
        columns = GridCells.Fixed(3),
        state = grid,
        userScrollEnabled = !dragging,
        modifier = Modifier.size(width = 300.dp, height = 600.dp).testTag("grid").dragSelection(controller) {},
    ) {
        items(keys, key = { it }) { Box(Modifier.aspectRatio(1f).testTag(it)) }
    }
}
