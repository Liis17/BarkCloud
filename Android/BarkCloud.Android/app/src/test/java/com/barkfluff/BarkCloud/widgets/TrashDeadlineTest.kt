package com.barkfluff.BarkCloud.widgets

import org.junit.Assert.assertEquals
import org.junit.Test

class TrashDeadlineTest {
    private val now = 1_700_000_000_000L
    private val hour = 60 * 60 * 1000L
    private val day = 24 * hour

    @Test
    fun `less than an hour left is soon`() {
        assertEquals(TrashDeadline.Soon, trashDeadline(now + hour - 1, now))
    }

    @Test
    fun `under a day shows whole hours`() {
        assertEquals(TrashDeadline.Hours(5), trashDeadline(now + 5 * hour + 30 * 60 * 1000L, now))
    }

    @Test
    fun `a day or more shows whole days rounded down`() {
        assertEquals(TrashDeadline.Days(1), trashDeadline(now + day, now))
        assertEquals(TrashDeadline.Days(29), trashDeadline(now + 29 * day + 23 * hour, now))
    }

    @Test
    fun `already overdue is soon`() {
        assertEquals(TrashDeadline.Soon, trashDeadline(now - day, now))
    }
}
