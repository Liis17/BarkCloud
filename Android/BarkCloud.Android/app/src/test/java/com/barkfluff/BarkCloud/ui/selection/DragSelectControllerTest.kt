package com.barkfluff.BarkCloud.ui.selection

import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DragSelectControllerTest {

    private val keys = (0 until 50).map { "k$it" }
    private val rowHeight = 100f
    private val viewportHeight = 500f

    /** Контент прокручивается на [contentOffset]; видимы только строки, пересекающие viewport. */
    private class Scene(val keys: List<String>, val rowHeight: Float, val viewportHeight: Float) {
        var contentOffset = 0f
        var selection: Set<String> = emptySet()
        val activeEvents = mutableListOf<Boolean>()

        fun cells(): List<HitCell<String>> = keys.mapIndexedNotNull { index, key ->
            val top = index * rowHeight - contentOffset
            val bottom = top + rowHeight
            if (bottom <= 0f || top >= viewportHeight) null else HitCell(key, 0f, top, 100f, bottom)
        }
    }

    @Test
    fun longPressStartsRangeAndMoveExtendsIt() = runTest {
        val scene = Scene(keys, rowHeight, viewportHeight)
        val controller = controller(this, scene)

        assertTrue(controller.start(50f, 250f)) // строка k2
        assertEquals(setOf("k2"), scene.selection)

        controller.move(50f, 450f) // строка k4
        assertEquals(setOf("k2", "k3", "k4"), scene.selection)
        controller.end()
    }

    @Test
    fun reversingShrinksSelectionButKeepsSnapshot() = runTest {
        val scene = Scene(keys, rowHeight, viewportHeight)
        scene.selection = setOf("k0")
        val controller = controller(this, scene)

        controller.start(50f, 250f) // k2
        controller.move(50f, 450f) // k4
        controller.move(50f, 150f) // k1

        assertEquals(setOf("k0", "k1", "k2"), scene.selection)
        controller.end()
    }

    @Test
    fun stationaryFingerNearBottomEdgeKeepsScrollingAndExtendingRange() = runTest {
        val scene = Scene(keys, rowHeight, viewportHeight)
        val controller = controller(this, scene)

        controller.start(50f, 50f) // k0
        controller.move(50f, 490f) // палец у нижнего края, не двигается дальше
        advanceTimeBy(1_000)

        assertTrue("content should scroll under stationary finger", scene.contentOffset > 0f)
        assertTrue("range should grow past first viewport", scene.selection.size > 10)
        controller.end()
    }

    @Test
    fun headerOrGapDoesNotStartGesture() = runTest {
        val scene = Scene(keys, rowHeight, viewportHeight)
        val controller = controller(this, scene)

        assertFalse(controller.start(50f, 900f))
        assertTrue(scene.activeEvents.isEmpty())
    }

    @Test
    fun removedAnchorEndsGestureWithoutChangingSelection() = runTest {
        val scene = Scene(keys, rowHeight, viewportHeight)
        var order = keys
        val controller = DragSelectController(
            scope = this,
            orderedKeys = { order },
            cells = { scene.cells() },
            viewport = { 0f to viewportHeight },
            selected = { scene.selection },
            applySelection = { scene.selection = it },
            scrollBy = { scene.contentOffset += it },
            onActiveChange = { scene.activeEvents += it },
            edgePx = 72f,
            maxStepPx = 24f,
        )
        controller.start(50f, 250f)
        controller.move(50f, 350f)
        val before = scene.selection

        order = keys - "k2"
        controller.move(50f, 450f)

        assertEquals(before, scene.selection)
        assertFalse(controller.isDragging)
        assertEquals(listOf(true, false), scene.activeEvents)
    }

    private fun controller(scope: kotlinx.coroutines.test.TestScope, scene: Scene) = DragSelectController(
        scope = scope,
        orderedKeys = { scene.keys },
        cells = { scene.cells() },
        viewport = { 0f to scene.viewportHeight },
        selected = { scene.selection },
        applySelection = { scene.selection = it },
        scrollBy = { scene.contentOffset += it },
        onActiveChange = { scene.activeEvents += it },
        edgePx = 72f,
        maxStepPx = 24f,
    )
}
