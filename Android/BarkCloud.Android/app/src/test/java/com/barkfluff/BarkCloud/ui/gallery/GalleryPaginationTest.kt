package com.barkfluff.BarkCloud.ui.gallery

import org.junit.Assert.assertEquals
import org.junit.Test

class GalleryPaginationTest {
    @Test fun `distance includes unshown rows and date headings`() {
        // Heading 0, first section media 1..6; heading 7, next section media 8..11.
        assertEquals(650, galleryRemainingPx(listOf(6, 4), 3, 3, -300, 300, 50))
    }

    @Test fun `partial final row counts as one row`() {
        assertEquals(500, galleryRemainingPx(listOf(7), 3, 3, -100, 300, 50))
    }

    @Test fun `last row uses actual viewport offset at threshold`() {
        assertEquals(500, galleryRemainingPx(listOf(6), 3, 6, 500, 300, 50))
        assertEquals(501, galleryRemainingPx(listOf(6), 3, 6, 501, 300, 50))
        assertEquals(-20, galleryRemainingPx(listOf(6), 3, 6, -20, 300, 50))
    }

    @Test fun `visible heading leaves all media rows remaining`() {
        assertEquals(600, galleryRemainingPx(listOf(4), 3, 0, 0, 300, 50))
    }
}
