package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cache.CacheInvalidatedException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
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
    fun actionVisibilityMatchesMediaAndViewerOptions() {
        val photoActions = cloudMediaActionVisibility(isVideo = false, infoEnabled = true, canDelete = true)
        assertTrue(photoActions.share)
        assertTrue(photoActions.copy)
        assertTrue(photoActions.download)
        assertTrue(photoActions.info)
        assertTrue(photoActions.delete)

        val videoActions = cloudMediaActionVisibility(isVideo = true, infoEnabled = false, canDelete = false)
        assertTrue(videoActions.share)
        assertFalse(videoActions.copy)
        assertTrue(videoActions.download)
        assertFalse(videoActions.info)
        assertFalse(videoActions.delete)
    }

    @Test
    fun mediaStoreTargetUsesImagesAndPicturesForPhotos() {
        assertEquals(
            CloudMediaStoreTarget(
                collection = CloudMediaStoreCollection.IMAGES,
                mimeType = "image/jpeg",
                relativePath = "Pictures/BarkCloud",
            ),
            cloudMediaStoreTarget(isVideo = false, mimeType = "image/jpeg"),
        )
    }

    @Test
    fun mediaStoreTargetUsesVideosAndMoviesForVideos() {
        assertEquals(
            CloudMediaStoreTarget(
                collection = CloudMediaStoreCollection.VIDEOS,
                mimeType = "video/mp4",
                relativePath = "Movies/BarkCloud",
            ),
            cloudMediaStoreTarget(isVideo = true, mimeType = "video/mp4"),
        )
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
