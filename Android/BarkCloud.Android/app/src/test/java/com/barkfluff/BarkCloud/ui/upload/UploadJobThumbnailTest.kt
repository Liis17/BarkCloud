package com.barkfluff.BarkCloud.ui.upload

import com.barkfluff.BarkCloud.data.cache.LocalMediaImageModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test
import java.io.IOException

class UploadJobThumbnailTest {
    @Test
    fun revokedMediaPermissionUsesStagedThumbnailFallback() = runTest {
        val fallback = LocalMediaImageModel(
            uri = "file:///staged.jpg",
            sourceId = "file:/staged.jpg",
            sourceVersion = "0:3",
            maxDimensionPx = 256,
            isVideo = false,
        )

        val result = loadUploadJobThumbnailModel("image:42", fallback) {
            throw SecurityException("READ_MEDIA permission revoked")
        }

        assertEquals(fallback, result)
    }

    @Test
    fun unavailableMediaWithoutStagedFileUsesPlaceholderModel() = runTest {
        val result = loadUploadJobThumbnailModel("image:42", fallback = null) {
            throw IOException("media provider unavailable")
        }

        assertNull(result)
    }

    @Test
    fun cancellationFromMediaLookupIsPropagated() = runTest {
        val cancellation = CancellationException("lookup cancelled")

        val thrown = try {
            loadUploadJobThumbnailModel("image:42", fallback = null) { throw cancellation }
            null
        } catch (error: CancellationException) {
            error
        }

        assertSame(cancellation, thrown)
    }
}
