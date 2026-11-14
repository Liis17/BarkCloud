package com.barkfluff.BarkCloud.ui.gallery

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CleanupWaveTest {
    @Test fun scrollAcceleratesFromRestToFullDistance() {
        assertEquals(0f, CleanupWave.scrollFraction(0f), 0f)
        assertEquals(1f, CleanupWave.scrollFraction(1f), 0f)
        val steps = (0..20).map { CleanupWave.scrollFraction(it / 20f) }
        val deltas = steps.zipWithNext { a, b -> b - a }
        assertTrue(deltas.all { it > 0f })
        assertTrue("смещение ускоряется", deltas.last() > deltas.first() * 5)
    }

    @Test fun timeAtFractionInvertsScroll() {
        for (i in 1..9) {
            val t = i / 10f + 0.03f
            assertEquals(t, CleanupWave.timeAtFraction(CleanupWave.scrollFraction(t)), 1e-4f)
        }
    }

    @Test fun eightRowsMatchMockupTimings() {
        val expected = floatArrayOf(0f, 0.16f, 0.44f, 0.57f, 0.67f, 0.78f, 0.85f, 0.94f)
        val flights = floatArrayOf(0.32f, 0.32f, 0.25f, 0.25f, 0.21f, 0.21f, 0.18f, 0.18f)
        expected.forEachIndexed { row, start ->
            assertEquals("старт ряда $row", start, CleanupWave.rowStart(row, 8), 0.02f)
            assertEquals("полёт ряда $row", flights[row], CleanupWave.rowFlight(row, 8), 0.01f)
        }
        assertEquals(CleanupWave.DONE_SECONDS, CleanupWave.totalSeconds(8), 0f)
    }

    @Test fun rowsStartInOrderForAnyGridHeight() {
        for (rows in 1..12) {
            val starts = (0 until rows).map { CleanupWave.rowStart(it, rows) }
            assertTrue(starts.zipWithNext().all { (a, b) -> b > a })
            assertTrue(CleanupWave.totalSeconds(rows) >= CleanupWave.DONE_SECONDS)
        }
    }
}
