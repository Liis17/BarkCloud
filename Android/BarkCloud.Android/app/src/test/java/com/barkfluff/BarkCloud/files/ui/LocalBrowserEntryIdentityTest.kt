package com.barkfluff.BarkCloud.files.ui

import com.barkfluff.BarkCloud.files.domain.FsEntry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class LocalBrowserEntryIdentityTest {
    private fun file(path: String) = FsEntry.File(
        path = path,
        name = path.substringAfterLast('/'),
        lastModified = 1_000L,
        sizeBytes = 10L,
        mimeType = "image/jpeg",
    )

    @Test
    fun hashCollidingPathsKeepDistinctViewerKeys() {
        val first = file("/storage/DCIM/Aa.jpg")
        val second = file("/storage/DCIM/BB.jpg")

        // Прежний ключ по хэшу пути совпадал у этой пары: hashCode одинаков.
        assertEquals(first.path.hashCode(), second.path.hashCode())
        assertNotEquals(localViewerStableKey(first), localViewerStableKey(second))
    }

    @Test
    fun keyIsStableForTheSamePath() {
        assertEquals(localViewerStableKey(file("/storage/DCIM/Aa.jpg")), localViewerStableKey(file("/storage/DCIM/Aa.jpg")))
    }
}
