package com.barkfluff.BarkCloud.data.cache

import coil3.decode.DataSource
import coil3.disk.DiskCache
import coil3.fetch.SourceFetchResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.test.runTest
import okio.FileSystem
import okio.Path
import okio.Path.Companion.toOkioPath
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files

class CloudImageFetcherTest {
    @Test
    fun cacheHitReturnsManagedFileWithoutReadingLegacyCacheOrDownloading() = runTest {
        val root = Files.createTempDirectory("cloud-image-hit-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val loader = RecordingLoader(byteArrayOf(1, 2, 3))
            val cache = cache(root, scope, loader)
            val model = CloudImageModel("https://cdn/preview", "file-1", "preview-1")
            val cached = cache.load(
                FileCacheKey.preview(scope, "file-1", "preview-1", model.url),
                "preview.jpg",
                model.url,
            )
            val networkRequestsBeforeFetch = loader.networkRequests.size
            val diskCache = FakeDiskCache(root)

            val result = fetcher(model, cache, scope, diskCache).fetch() as SourceFetchResult

            assertEquals(cached.absolutePath, File(result.source.file().toString()).absolutePath)
            assertEquals(DataSource.DISK, result.dataSource)
            assertEquals(networkRequestsBeforeFetch, loader.networkRequests.size)
            assertTrue(diskCache.openedKeys.isEmpty())
            assertTrue(diskCache.editedKeys.isEmpty())
            result.source.close()
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun legacyDiskCacheEntryIsCopiedIntoManagedCacheIndexedAndRemovedFromLegacyCache() = runTest {
        val root = Files.createTempDirectory("cloud-image-legacy-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val loader = RecordingLoader(byteArrayOf(9, 9))
            val cache = cache(root, scope, loader)
            val url = "https://cdn/legacy-preview"
            val model = CloudImageModel(url, "file-2", "preview-2")
            val legacyFile = File(root, "legacy.jpg").apply {
                writeBytes(byteArrayOf(4, 5, 6))
            }
            val diskCache = FakeDiskCache(root).apply {
                snapshotKey = url
                snapshot = FakeSnapshot(legacyFile.toOkioPath())
            }

            val result = fetcher(model, cache, scope, diskCache).fetch() as SourceFetchResult
            val indexed = cache.get(FileCacheKey.preview(scope, "file-2", "preview-2", url))

            assertEquals(listOf(url), diskCache.openedKeys)
            assertTrue(diskCache.editedKeys.isEmpty())
            assertEquals(listOf(url), diskCache.removedKeys)
            assertEquals(0, loader.networkRequests.size)
            assertEquals(1, cache.entryCount())
            assertEquals(DataSource.DISK, result.dataSource)
            assertArrayEquals(byteArrayOf(4, 5, 6), requireNotNull(indexed).readBytes())
            assertTrue(requireNotNull(diskCache.snapshot).closed)
            result.source.close()
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun failedLegacyTransferKeepsEntryAndFallsBackToNetwork() = runTest {
        val root = Files.createTempDirectory("cloud-image-legacy-failure-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val loader = RecordingLoader(byteArrayOf(7, 8, 9), failSourceFile = true)
            val cache = cache(root, scope, loader)
            val url = "https://cdn/legacy-preview"
            val model = CloudImageModel(url, "file-legacy-failure", "preview-legacy-failure")
            val legacyFile = File(root, "legacy.jpg").apply { writeBytes(byteArrayOf(4, 5, 6)) }
            val diskCache = FakeDiskCache(root).apply {
                snapshotKey = url
                snapshot = FakeSnapshot(legacyFile.toOkioPath())
            }

            val result = fetcher(model, cache, scope, diskCache).fetch() as SourceFetchResult

            assertEquals(listOf(url), diskCache.openedKeys)
            assertTrue(diskCache.removedKeys.isEmpty())
            assertEquals(listOf(url), loader.networkRequests)
            assertEquals(DataSource.NETWORK, result.dataSource)
            assertTrue(requireNotNull(diskCache.snapshot).closed)
            result.source.close()
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun cacheMissLoadsUrlAndDoesNotWriteToCoilDiskCache() = runTest {
        val root = Files.createTempDirectory("cloud-image-miss-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val loader = RecordingLoader(byteArrayOf(7, 8, 9))
            val cache = cache(root, scope, loader)
            val url = "https://cdn/new-preview"
            val diskCache = FakeDiskCache(root)

            val result = fetcher(CloudImageModel(url, "file-3", "preview-3"), cache, scope, diskCache)
                .fetch() as SourceFetchResult
            val managedFile = cache.get(FileCacheKey.preview(scope, "file-3", "preview-3", url))

            assertEquals(listOf(url), loader.networkRequests)
            assertEquals(listOf(url), diskCache.openedKeys)
            assertTrue(diskCache.editedKeys.isEmpty())
            assertEquals(DataSource.NETWORK, result.dataSource)
            assertArrayEquals(byteArrayOf(7, 8, 9), requireNotNull(managedFile).readBytes())
            result.source.close()
        } finally {
            root.deleteRecursively()
        }
    }

    private fun fetcher(
        model: CloudImageModel,
        cache: FileCacheService,
        scope: CacheScope,
        diskCache: DiskCache,
    ) = CloudImageFetcher(model, cache, { scope }, diskCache, FileSystem.SYSTEM)

    private fun cache(root: File, scope: CacheScope, loader: RecordingLoader) = FileCacheService(
        baseDirectory = root,
        index = MemoryIndex(),
        loader = loader,
        settings = TestPolicy(),
        currentScope = { scope },
        ioDispatcher = Dispatchers.IO,
    )

    private class RecordingLoader(
        private val response: ByteArray,
        private val failSourceFile: Boolean = false,
    ) : FileCacheLoader {
        val networkRequests = mutableListOf<String>()

        override suspend fun load(request: FileCacheLoadRequest, destination: File) {
            val source = request.sourceFile
            if (source != null) {
                if (failSourceFile) throw IOException("legacy copy failed")
                source.copyTo(destination, overwrite = true)
            } else {
                networkRequests += requireNotNull(request.sourceUrl)
                destination.writeBytes(response)
            }
        }
    }

    private class MemoryIndex : FileCacheIndex {
        private val entries = mutableMapOf<String, FileCacheEntry>()

        override suspend fun get(storageKey: String): FileCacheEntry? = entries[storageKey]
        override suspend fun all(): List<FileCacheEntry> = entries.values.toList()
        override suspend fun upsert(entry: FileCacheEntry) {
            entries[entry.key.storageKey] = entry
        }
        override suspend fun delete(storageKey: String) {
            entries.remove(storageKey)
        }
        override suspend fun clear() {
            entries.clear()
        }
    }

    private class TestPolicy : FileCachePolicy {
        override val maxCacheBytes = 1024L * 1024L
        override val staleMaxAgeMillis = Long.MAX_VALUE
        override var lastSweepAtMillis = 0L
    }

    private class FakeDiskCache(private val root: File) : DiskCache {
        override val size: Long = 0L
        override val maxSize: Long = 1024L
        override val directory: Path = File(root, "legacy-coil").apply { mkdirs() }.toOkioPath()
        override val fileSystem: FileSystem = FileSystem.SYSTEM
        var snapshotKey: String? = null
        var snapshot: FakeSnapshot? = null
        val openedKeys = mutableListOf<String>()
        val editedKeys = mutableListOf<String>()
        val removedKeys = mutableListOf<String>()

        override fun openSnapshot(key: String): DiskCache.Snapshot? {
            openedKeys += key
            return snapshot.takeIf { key == snapshotKey }
        }

        override fun openEditor(key: String): DiskCache.Editor? {
            editedKeys += key
            return null
        }

        override fun remove(key: String): Boolean {
            removedKeys += key
            return true
        }
        override fun clear() = Unit
        override fun shutdown() = Unit
    }

    private class FakeSnapshot(override val data: Path) : DiskCache.Snapshot {
        override val metadata: Path = data
        var closed = false

        override fun close() {
            closed = true
        }

        override fun closeAndOpenEditor(): DiskCache.Editor? = throw IOException("unexpected edit")
    }
}
