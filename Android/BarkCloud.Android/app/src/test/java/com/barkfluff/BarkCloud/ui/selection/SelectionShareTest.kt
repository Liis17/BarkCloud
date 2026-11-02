package com.barkfluff.BarkCloud.ui.selection

import org.junit.Assert.assertEquals
import org.junit.Test

class SelectionShareTest {

    @Test
    fun sameTypeKeepsExactMime() {
        assertEquals("image/jpeg", shareMimeType(listOf("image/jpeg", "IMAGE/JPEG")))
    }

    @Test
    fun sameGroupUsesWildcardSubtype() {
        assertEquals("image/*", shareMimeType(listOf("image/jpeg", "image/png")))
    }

    @Test
    fun mixedPhotoAndVideoFallsBackToAnyType() {
        assertEquals("*/*", shareMimeType(listOf("image/jpeg", "video/mp4")))
    }
}
