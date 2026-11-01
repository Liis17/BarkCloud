package com.barkfluff.BarkCloud.data.upload

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadRunTallyTest {
    @Test
    fun totalIsDonePlusRemainingPlusRunFailures() {
        val tally = UploadRunTally(done = 2, remaining = 5, failed = 1)

        assertEquals(8, tally.total)
        assertEquals(25, tally.percent)
        assertEquals("2/8", tally.chipText())
    }

    @Test
    fun chipKeepsFractionWhenItFitsSixCharactersAndFallsBackToPercent() {
        assertEquals("10/100", UploadRunTally(done = 10, remaining = 90, failed = 0).chipText())
        assertEquals("10%", UploadRunTally(done = 100, remaining = 900, failed = 0).chipText())
    }

    @Test
    fun emptyTallyHasZeroPercent() {
        assertEquals(0, UploadRunTally(done = 0, remaining = 0, failed = 0).percent)
    }

    @Test
    fun onlyFullyCompletedRunWithoutErrorsIsGenuineSuccess() {
        assertTrue(UploadRunTally(done = 3, remaining = 0, failed = 0).isGenuineSuccess)
        assertFalse(UploadRunTally(done = 0, remaining = 0, failed = 0).isGenuineSuccess)
        assertFalse(UploadRunTally(done = 3, remaining = 0, failed = 1).isGenuineSuccess)
        assertFalse(UploadRunTally(done = 3, remaining = 1, failed = 0).isGenuineSuccess)
    }
}
