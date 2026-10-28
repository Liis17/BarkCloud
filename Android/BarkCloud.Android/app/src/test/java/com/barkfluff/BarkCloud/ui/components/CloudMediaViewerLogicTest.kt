package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cache.CacheInvalidatedException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

class CloudMediaViewerLogicTest {
    @Test
    fun unsupportedImageExtensionsUseJpegViewWhenAvailable() {
        listOf("photo.HEIC", "scan.heif", "drawing.svg", "image.tiff", "image.tif").forEach { name ->
            assertTrue(name, requiresJpegView(name, hasJpegView = true))
        }
    }

    @Test
    fun jpegViewIsNotSelectedWhenMissingOrExtensionIsSupported() {
        assertFalse(requiresJpegView("photo.heic", hasJpegView = false))
        assertFalse(requiresJpegView("photo.jpg", hasJpegView = true))
        assertFalse(requiresJpegView("photo.PNG", hasJpegView = true))
    }

    @Test
    fun decodeRetryInvalidatesTheCachedRepresentationThatFailed() {
        assertTrue(viewerCacheKindAfterDecodeFailure(useJpegView = false) == ViewerCacheKind.ORIGINAL)
        assertTrue(viewerCacheKindAfterDecodeFailure(useJpegView = true) == ViewerCacheKind.JPEG_VIEW)
    }

    @Test
    fun cacheInvalidationErrorsDoNotEscapeTheViewerCoroutine() = runBlocking {
        var continued = false

        invalidateViewerCacheSafely { throw CacheInvalidatedException("scope changed") }
        invalidateViewerCacheSafely { throw IOException("index failed") }
        continued = true

        assertTrue(continued)
    }
}
