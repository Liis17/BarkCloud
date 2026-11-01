package com.barkfluff.BarkCloud.data.cloud

import com.barkfluff.BarkCloud.data.cache.FileCachePin
import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.cache.PinnedFile
import com.barkfluff.BarkCloud.net.FileTransferService
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException

class VideoPlaybackResolverTest {
    private val asset = MediaAsset(
        id = "video-1",
        fileName = "clip.mp4",
        fileSize = 100L,
        kind = CloudMediaKind.VIDEO,
        previews = emptyList(),
        createdAtMillis = 0L,
        origin = CloudMediaOrigin.OWN,
    )

    @Test
    fun ownVideoWithoutCacheStreamsFreshTempUrl() = runBlocking {
        val cache = mockk<FileCacheService>()
        val transfer = mockk<FileTransferService>()
        coEvery { cache.pinCachedOriginal("video-1", false) } returns null
        coEvery { transfer.tempDownloadUrls(listOf("video-1")) } returns mapOf("video-1" to "https://temp.example/v")
        var sharedCalls = 0
        val resolver = VideoPlaybackResolver(cache, transfer) { sharedCalls++; "unused" }

        val resolved = resolver.resolve(asset)

        assertEquals(PlaybackSource.Remote("https://temp.example/v"), resolved.source)
        assertEquals(0, sharedCalls)
    }

    @Test
    fun cachedOwnVideoPlaysFromPinnedFileWithoutRequestingAUrl() = runBlocking {
        val cache = mockk<FileCacheService>()
        val transfer = mockk<FileTransferService>()
        val file = File("cached-video.mp4")
        coEvery { cache.pinCachedOriginal("video-1", false) } returns PinnedFile(file, FileCachePin(1L))

        val resolved = VideoPlaybackResolver(cache, transfer) { error("shared RPC must not run for own files") }.resolve(asset)

        assertTrue(resolved.source is PlaybackSource.Local)
        coVerify(exactly = 0) { transfer.tempDownloadUrls(any()) }
    }

    @Test
    fun sharedVideoAuthorizesBeforePinningTheCachedCopy() = runBlocking {
        val cache = mockk<FileCacheService>()
        val transfer = mockk<FileTransferService>()
        val order = mutableListOf<String>()
        val shared = asset.copy(origin = CloudMediaOrigin.SHARED)
        coEvery { cache.pinCachedOriginal("video-1", true) } answers {
            order += "pin"
            null
        }

        val resolver = VideoPlaybackResolver(cache, transfer) { fileId ->
            order += "rpc:$fileId"
            "https://signed.example/shared"
        }
        val resolved = resolver.resolve(shared)

        assertEquals(listOf("rpc:video-1", "pin"), order)
        assertEquals(PlaybackSource.Remote("https://signed.example/shared"), resolved.source)
        coVerify(exactly = 0) { transfer.tempDownloadUrls(any()) }
    }

    @Test
    fun revokedSharedAccessFailsBeforeTouchingTheCache() {
        val cache = mockk<FileCacheService>()
        val transfer = mockk<FileTransferService>()
        val shared = asset.copy(origin = CloudMediaOrigin.SHARED)

        assertThrows(IOException::class.java) {
            runBlocking {
                VideoPlaybackResolver(cache, transfer) { throw IOException("revoked") }.resolve(shared)
            }
        }
        coVerify(exactly = 0) { cache.pinCachedOriginal(any(), any()) }
    }
}
