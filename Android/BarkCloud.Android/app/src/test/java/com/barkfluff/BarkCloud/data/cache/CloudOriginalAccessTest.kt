package com.barkfluff.BarkCloud.data.cache

import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.CloudMediaOrigin
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.io.File
import java.io.IOException

class CloudOriginalAccessTest {
    private val cached = File("cached-original.jpg")

    private fun asset(origin: CloudMediaOrigin) = MediaAsset(
        id = "file-1",
        fileName = "photo.jpg",
        fileSize = 10L,
        kind = CloudMediaKind.PHOTO,
        previews = emptyList(),
        createdAtMillis = 0L,
        origin = origin,
    )

    @Test
    fun ownOriginalUsesOwnCacheKeyAndNeverRequestsSharedUrl() = runBlocking {
        val cache = mockk<FileCacheService>()
        coEvery { cache.withLoadedPinnedOriginal<String>(any(), any(), any()) } coAnswers {
            thirdArg<suspend (File) -> String>().invoke(cached)
        }
        var sharedCalls = 0

        val result = withCloudOriginal(asset(CloudMediaOrigin.OWN), cache, { sharedCalls++; "https://unused" }) { it.path }

        assertEquals(cached.path, result)
        assertEquals(0, sharedCalls)
        coVerify(exactly = 0) { cache.withLoadedPinnedSharedOriginal<Any>(any(), any(), any(), any()) }
    }

    @Test
    fun sharedOriginalAuthorizesBeforeTouchingSharedCacheKey() = runBlocking {
        val cache = mockk<FileCacheService>()
        val order = mutableListOf<String>()
        coEvery { cache.withLoadedPinnedSharedOriginal<String>(any(), any(), any(), any()) } coAnswers {
            order += "cache:${secondArg<String>()}:${thirdArg<String>()}"
            arg<suspend (File) -> String>(3).invoke(cached)
        }

        val result = withCloudOriginal(
            asset(CloudMediaOrigin.SHARED),
            cache,
            { fileId -> order += "rpc:$fileId"; "https://signed.example/share" },
        ) { it.path }

        assertEquals(cached.path, result)
        assertEquals(listOf("rpc:file-1", "cache:photo.jpg:https://signed.example/share"), order)
        coVerify(exactly = 0) { cache.withLoadedPinnedOriginal<Any>(any(), any(), any()) }
    }

    @Test
    fun failedSharedAuthorizationBlocksEvenCachedSharedOriginal() {
        val cache = mockk<FileCacheService>()
        assertThrows(IOException::class.java) {
            runBlocking {
                withCloudOriginal(
                    asset(CloudMediaOrigin.SHARED),
                    cache,
                    { throw IOException("revoked") },
                ) { it.path }
            }
        }
        coVerify(exactly = 0) { cache.withLoadedPinnedSharedOriginal<Any>(any(), any(), any(), any()) }
        coVerify(exactly = 0) { cache.withLoadedPinnedOriginal<Any>(any(), any(), any()) }
    }

    @Test
    fun revokedSharedJpegViewIsNeverReadFromTheCache() {
        val cache = mockk<FileCacheService>()
        val model = CloudImageModel(url = "https://jpeg.example/view", fileId = "file-1", previewFileId = "view-1")

        assertThrows(IOException::class.java) {
            runBlocking {
                withCloudJpegView(asset(CloudMediaOrigin.SHARED), cache, { throw IOException("revoked") }, model) { it.path }
            }
        }

        coVerify(exactly = 0) { cache.withLoadedPinnedJpegView<Any>(any(), any(), any(), any()) }
    }

    @Test
    fun sharedJpegViewAuthorizesBeforeTheCachedRead() = runBlocking {
        val cache = mockk<FileCacheService>()
        val model = CloudImageModel(url = "https://jpeg.example/view", fileId = "file-1", previewFileId = "view-1")
        val order = mutableListOf<String>()
        coEvery { cache.withLoadedPinnedJpegView<String>(any(), any(), any(), any()) } coAnswers {
            order += "cache:${secondArg<String>()}"
            arg<suspend (File) -> String>(3).invoke(cached)
        }

        val result = withCloudJpegView(
            asset(CloudMediaOrigin.SHARED),
            cache,
            { fileId -> order += "rpc:$fileId"; "https://signed.example/original" },
            model,
        ) { it.path }

        assertEquals(cached.path, result)
        assertEquals(listOf("rpc:file-1", "cache:https://jpeg.example/view"), order)
    }

    @Test
    fun ownJpegViewDoesNotRequestSharedAuthorization() = runBlocking<Unit> {
        val cache = mockk<FileCacheService>()
        val model = CloudImageModel(url = "https://jpeg.example/view", fileId = "file-1", previewFileId = "view-1")
        coEvery { cache.withLoadedPinnedJpegView<String>(any(), any(), any(), any()) } coAnswers {
            arg<suspend (File) -> String>(3).invoke(cached)
        }

        withCloudJpegView(asset(CloudMediaOrigin.OWN), cache, { error("shared RPC must not run") }, model) { it.path }
    }
}
