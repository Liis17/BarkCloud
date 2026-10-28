package com.barkfluff.BarkCloud.ui.settings

import com.barkfluff.BarkCloud.data.cache.CacheCategory
import com.barkfluff.BarkCloud.data.cache.CacheCategoryStats
import com.barkfluff.BarkCloud.data.cache.FileCacheStats
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CacheSettingsMappingTest {
    @Test
    fun statisticsMapJpegViewsWithPreviewsAndKeepLegacySeparate() {
        val state = cacheStatisticsToUiState(
            stats = FileCacheStats(
                categories = mapOf(
                    CacheCategory.PREVIEW to CacheCategoryStats(120, 3),
                    CacheCategory.ORIGINAL to CacheCategoryStats(800, 2),
                    CacheCategory.JPEG_VIEW to CacheCategoryStats(70, 1),
                ),
                totalSizeBytes = 990,
                entryCount = 6,
                isOverLimit = true,
                legacyPreviewSizeBytes = 35,
                legacyPreviewEntryCount = 4,
            ),
            maxCacheBytes = 500,
            staleMaxAgeMillis = 7_000,
        )

        assertEquals(990, state.cacheSize)
        assertEquals(6, state.entryCount)
        assertEquals(190, state.previewCacheSize)
        assertEquals(4, state.previewEntryCount)
        assertEquals(800, state.originalCacheSize)
        assertEquals(2, state.originalEntryCount)
        assertEquals(35, state.legacyPreviewSize)
        assertEquals(4, state.legacyPreviewEntryCount)
        assertTrue(state.isOverLimit)
        assertEquals(500, state.maxCacheBytes)
        assertEquals(7_000, state.staleMaxAgeMillis)
    }
}
