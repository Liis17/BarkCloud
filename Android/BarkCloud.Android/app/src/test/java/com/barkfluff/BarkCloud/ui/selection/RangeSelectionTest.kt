package com.barkfluff.BarkCloud.ui.selection

import org.junit.Assert.assertEquals
import org.junit.Test

class RangeSelectionTest {

    private val keys = (0 until 10).map { "k$it" }

    @Test
    fun rangeIncludesBothEndpoints() {
        val result = rangeSelection(keys, base = emptySet(), anchor = "k2", current = "k4")

        assertEquals(setOf("k2", "k3", "k4"), result)
    }

    @Test
    fun reversingTowardAnchorShrinksRange() {
        val forward = rangeSelection(keys, emptySet(), anchor = "k2", current = "k7")
        val reversed = rangeSelection(keys, emptySet(), anchor = "k2", current = "k4")

        assertEquals((2..7).map { "k$it" }.toSet(), forward)
        assertEquals((2..4).map { "k$it" }.toSet(), reversed)
    }

    @Test
    fun dragBeforeAnchorSelectsUpwardRange() {
        val result = rangeSelection(keys, emptySet(), anchor = "k6", current = "k3")

        assertEquals((3..6).map { "k$it" }.toSet(), result)
    }

    @Test
    fun selectionOutsideRangeFromSnapshotIsRetained() {
        val base = setOf("k0", "k9")

        val result = rangeSelection(keys, base, anchor = "k3", current = "k4")

        assertEquals(setOf("k0", "k9", "k3", "k4"), result)
    }

    @Test
    fun snapshotIsNotReducedByShrinkingBelowItsItems() {
        val base = setOf("k5")
        val first = rangeSelection(keys, base, anchor = "k5", current = "k8")
        val second = rangeSelection(keys, base, anchor = "k5", current = "k5")

        assertEquals(setOf("k5", "k6", "k7", "k8"), first)
        assertEquals(setOf("k5"), second)
    }

    @Test
    fun missingAnchorKeepsOnlyBaseAndAnchor() {
        val result = rangeSelection(keys, base = setOf("k1"), anchor = "gone", current = "k3")

        assertEquals(setOf("k1", "gone"), result)
    }

    @Test
    fun hitKeyIgnoresGapsAndHeaders() {
        val cells = listOf(
            HitCell("a", left = 0f, top = 0f, right = 100f, bottom = 100f),
            HitCell("b", left = 110f, top = 0f, right = 210f, bottom = 100f),
        )

        assertEquals("a", hitKey(cells, 0f, 0f))
        assertEquals("a", hitKey(cells, 99.9f, 99.9f))
        assertEquals(null, hitKey(cells, 100f, 50f))
        assertEquals(null, hitKey(cells, 50f, 150f))
        assertEquals("b", hitKey(cells, 110f, 10f))
    }

    @Test
    fun autoScrollIsZeroInsideViewport() {
        assertEquals(0f, autoScrollStep(pointerY = 500f, top = 0f, bottom = 1000f, edgePx = 100f, maxStepPx = 20f), 0f)
    }

    @Test
    fun autoScrollSpeedGrowsTowardTopEdge() {
        val nearEdge = autoScrollStep(pointerY = 90f, top = 0f, bottom = 1000f, edgePx = 100f, maxStepPx = 20f)
        val atEdge = autoScrollStep(pointerY = 10f, top = 0f, bottom = 1000f, edgePx = 100f, maxStepPx = 20f)

        assertEquals(-2f, nearEdge, 0.001f)
        assertEquals(-18f, atEdge, 0.001f)
    }

    @Test
    fun autoScrollClampsOutsideViewportBothWays() {
        assertEquals(-20f, autoScrollStep(-50f, 0f, 1000f, 100f, 20f), 0.001f)
        assertEquals(20f, autoScrollStep(1200f, 0f, 1000f, 100f, 20f), 0.001f)
    }

    @Test
    fun autoScrollBottomEdgeScrollsDown() {
        assertEquals(10f, autoScrollStep(pointerY = 950f, top = 0f, bottom = 1000f, edgePx = 100f, maxStepPx = 20f), 0.001f)
    }
}
