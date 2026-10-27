package com.barkfluff.BarkCloud.data.cache

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files

class LocalShareFileCacheTest {
    @Test
    fun repeatedShareReusesCopyAndPreservesSourceBytes() {
        val root = Files.createTempDirectory("share-cache-").toFile()
        try {
            val source = File(root, "source.bin").apply { writeBytes(byteArrayOf(1, 2, 3, 4)) }
            val cacheRoot = File(root, "cache").apply { mkdirs() }

            val first = LocalShareFileCache.prepare(cacheRoot, source)
            val second = LocalShareFileCache.prepare(cacheRoot, source)

            assertEquals(first, second)
            assertEquals(source.name, second.name)
            assertArrayEquals(source.readBytes(), second.readBytes())
            assertEquals(1, File(cacheRoot, LocalShareFileCache.DIRECTORY_NAME).listFiles()?.size)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun removesExpiredCopiesAndExplicitCacheClearRemovesShareDirectory() {
        val root = Files.createTempDirectory("share-cache-expiry-").toFile()
        try {
            val source = File(root, "source.bin").apply { writeBytes(byteArrayOf(5, 6)) }
            val otherSource = File(root, "other.bin").apply { writeBytes(byteArrayOf(7, 8)) }
            val cacheRoot = File(root, "cache").apply { mkdirs() }
            val now = System.currentTimeMillis()
            val oldCopy = LocalShareFileCache.prepare(cacheRoot, source, nowMillis = now)
            oldCopy.setLastModified(now - LocalShareFileCache.RETENTION_MILLIS + 60_000)
            requireNotNull(oldCopy.parentFile).setLastModified(now - LocalShareFileCache.RETENTION_MILLIS + 60_000)

            val freshCopy = LocalShareFileCache.prepare(cacheRoot, otherSource, nowMillis = now)

            assertTrue("a share within its retention window was removed", oldCopy.isFile)
            assertTrue(freshCopy.isFile)
            oldCopy.setLastModified(now - LocalShareFileCache.RETENTION_MILLIS - 60_000)
            requireNotNull(oldCopy.parentFile).setLastModified(now - LocalShareFileCache.RETENTION_MILLIS - 60_000)
            LocalShareFileCache.prepare(cacheRoot, File(root, "third.bin").apply { writeBytes(byteArrayOf(9)) }, nowMillis = now)
            assertFalse("an expired share was retained", oldCopy.exists())
            LocalShareFileCache.clear(cacheRoot)
            assertFalse(File(cacheRoot, LocalShareFileCache.DIRECTORY_NAME).exists())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun removesExpiredDirectoriesCreatedByOlderSharingCode() {
        val root = Files.createTempDirectory("share-cache-legacy-").toFile()
        try {
            val now = System.currentTimeMillis()
            val cacheRoot = File(root, "cache").apply { mkdirs() }
            val legacy = File(cacheRoot, "${LocalShareFileCache.DIRECTORY_NAME}/share-old").apply { mkdirs() }
            File(legacy, "video.mp4").writeBytes(byteArrayOf(1, 2))
            legacy.setLastModified(now - LocalShareFileCache.RETENTION_MILLIS - 60_000)

            val source = File(root, "photo.jpg").apply { writeBytes(byteArrayOf(3)) }
            LocalShareFileCache.prepare(cacheRoot, source, nowMillis = now)

            assertFalse(legacy.exists())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun missingSourceReportsAnIoFailure() {
        val root = Files.createTempDirectory("share-cache-missing-").toFile()
        try {
            val failure = runCatching {
                LocalShareFileCache.prepare(File(root, "cache"), File(root, "missing.jpg"))
            }.exceptionOrNull()

            assertTrue(failure is IOException)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun removesPartialCopyWhenCopyFails() {
        val root = Files.createTempDirectory("share-cache-failure-").toFile()
        try {
            val source = File(root, "source.bin").apply { writeBytes(byteArrayOf(7, 8, 9)) }
            val cacheRoot = File(root, "cache").apply { mkdirs() }
            val shareDir = File(cacheRoot, LocalShareFileCache.DIRECTORY_NAME)

            val error = runCatching {
                LocalShareFileCache.prepare(cacheRoot, source) { _, partial ->
                    partial.writeBytes(byteArrayOf(7))
                    throw IOException("copy failed")
                }
            }.exceptionOrNull()

            assertTrue(error is IOException)
            assertTrue(shareDir.listFiles().orEmpty().isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }
}
