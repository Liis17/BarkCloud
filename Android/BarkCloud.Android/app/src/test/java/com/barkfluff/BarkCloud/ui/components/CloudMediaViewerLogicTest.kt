package com.barkfluff.BarkCloud.ui.components

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

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
}
