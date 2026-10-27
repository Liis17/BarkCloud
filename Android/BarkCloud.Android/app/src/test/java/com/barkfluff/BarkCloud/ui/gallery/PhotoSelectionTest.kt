package com.barkfluff.BarkCloud.ui.gallery

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PhotoSelectionTest {
    @Test
    fun selectionStartsTogglesAndExitsWhenLastItemIsRemoved() {
        val selected = PhotoSelection().start("device:image:1")
        assertTrue(selected.isSelecting)

        val empty = selected.toggle("device:image:1")
        assertFalse(empty.isSelecting)
        assertTrue(empty.keys.isEmpty())
    }

    @Test
    fun mixedSelectionCountsOnlyActionsForMatchingSources() {
        val actions = photoSelectionActions(
            setOf("device:image:1", "device:video:2", "cloud:file-1"),
        )

        assertEquals(2, actions.uploadCount)
        assertEquals(1, actions.favoriteCount)
        assertEquals(3, actions.deleteCount)
    }

    @Test
    fun deleteActionsIncludeOnlyAvailableSourcesAndCombinedOption() {
        assertEquals(
            listOf(PhotoDeleteAction.CLOUD),
            photoDeleteActions(PhotoSelectionActions(deviceCount = 0, cloudCount = 2)),
        )
        assertEquals(
            listOf(PhotoDeleteAction.DEVICE, PhotoDeleteAction.CLOUD, PhotoDeleteAction.BOTH),
            photoDeleteActions(PhotoSelectionActions(deviceCount = 1, cloudCount = 2)),
        )
    }
}
