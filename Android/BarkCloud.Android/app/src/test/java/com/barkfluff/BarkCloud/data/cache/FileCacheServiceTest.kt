package com.barkfluff.BarkCloud.data.cache

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

@OptIn(ExperimentalCoroutinesApi::class)
class FileCacheServiceTest {
    @Test
    fun defaultCacheLimitsRemainFiveGiBAndSevenDays() {
        assertEquals(5L * 1024L * 1024L * 1024L, FileCacheSettings.DEFAULT_MAX_BYTES)
        assertEquals(7L * FileCacheSettings.DAY_MILLIS, FileCacheSettings.DEFAULT_STALE_AGE_MILLIS)
    }

    @Test
    fun repeatedAccessAndServiceRestartReuseCachedFileWithoutNetwork() = runTest {
        val root = Files.createTempDirectory("file-cache-restart-").toFile()
        try {
            val scope = CacheScope("https://one.example|443", "42")
            val firstLoader = RecordingLoader { _, destination -> destination.writeBytes(byteArrayOf(1, 2, 3)) }
            val first = cache(root, firstLoader, scope)
            val key = FileCacheKey.original(scope, "file-1")

            val saved = first.load(key, "photo.jpg")
            val repeated = first.load(key, "photo.jpg")
            val restarted = cache(root, RecordingLoader { _, _ -> error("network must not be used") }, scope)
            val afterRestart = restarted.load(key, "photo.jpg")

            assertEquals(saved, repeated)
            assertEquals(saved, afterRestart)
            assertEquals(1, firstLoader.calls.get())
            assertEquals(byteArrayOf(1, 2, 3).toList(), afterRestart.readBytes().toList())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun concurrentRequestsForSameKeyShareOneLoad() = runTest {
        val root = Files.createTempDirectory("file-cache-single-flight-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val started = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val loader = RecordingLoader { _, destination ->
                started.complete(Unit)
                release.await()
                destination.writeBytes(byteArrayOf(4, 5, 6))
            }
            val cache = cache(
                root,
                loader,
                scope,
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
            )
            val key = FileCacheKey.original(scope, "same-file")

            val first = async(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "same.bin") }
            started.await()
            val second = async(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "same.bin") }
            release.complete(Unit)

            assertEquals(first.await(), second.await())
            assertEquals(1, loader.calls.get())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun previewVariantsAndServerAccountScopesHaveSeparateEntries() = runTest {
        val root = Files.createTempDirectory("file-cache-scope-").toFile()
        try {
            var activeScope = CacheScope("server-a", "user-a")
            val loader = RecordingLoader { request, destination ->
                val source = request.sourceUrl ?: "${request.key.scope.serverKey}/${request.key.scope.accountId}"
                destination.writeBytes(source.encodeToByteArray())
            }
            val cache = cache(root, loader) { activeScope }

            val small = FileCacheKey.preview(activeScope, "photo", "preview-small", "https://cdn/small")
            val large = FileCacheKey.preview(activeScope, "photo", "preview-large", "https://cdn/large")
            val urlVariantOne = FileCacheKey.preview(activeScope, "photo", null, "https://cdn/a?size=128")
            val urlVariantTwo = FileCacheKey.preview(activeScope, "photo", null, "https://cdn/a?size=512")
            val smallFile = cache.load(small, "small.jpg", "https://cdn/small")
            val largeFile = cache.load(large, "large.jpg", "https://cdn/large")
            cache.load(urlVariantOne, "fallback.jpg", "https://cdn/a?size=128")
            cache.load(urlVariantTwo, "fallback.jpg", "https://cdn/a?size=512")

            activeScope = CacheScope("server-b", "user-a")
            val serverFile = cache.load(FileCacheKey.original(activeScope, "shared-id"), "same.dat")
            activeScope = CacheScope("server-b", "user-b")
            val accountFile = cache.load(FileCacheKey.original(activeScope, "shared-id"), "same.dat")

            assertFalse(smallFile == largeFile)
            assertFalse(serverFile == accountFile)
            assertEquals(6, cache.entryCount())
            assertEquals(6, loader.calls.get())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun previewIdFallbackJpegViewAndSharedOriginalUseDistinctKeys() = runTest {
        val root = Files.createTempDirectory("file-cache-variants-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val loader = RecordingLoader { request, destination ->
                destination.writeBytes((request.sourceUrl ?: request.key.variant).encodeToByteArray())
            }
            val cache = cache(root, loader, scope)
            val withPreviewId = FileCacheKey.preview(scope, "photo", "preview-1", "https://cdn/preview")
            val urlFallback = FileCacheKey.preview(scope, "photo", null, "https://cdn/preview")
            val jpegView = FileCacheKey.jpegView(scope, "photo")
            val firstJpegView = FileCacheKey.jpegView(scope, "photo", "jpeg-view-1")
            val regeneratedJpegView = FileCacheKey.jpegView(scope, "photo", "jpeg-view-2")
            val ownOriginal = FileCacheKey.original(scope, "shared-file")
            val sharedOriginal = FileCacheKey.sharedOriginal(scope, "shared-file")

            assertEquals(5, listOf(withPreviewId, urlFallback, jpegView, ownOriginal, sharedOriginal)
                .map { it.storageKey }
                .toSet()
                .size)
            assertFalse(firstJpegView.storageKey == regeneratedJpegView.storageKey)
            cache.load(withPreviewId, "preview.jpg", "https://cdn/preview")
            cache.load(urlFallback, "preview.jpg", "https://cdn/preview")
            cache.load(jpegView, "view.jpg", "https://cdn/view")
            cache.load(ownOriginal, "shared.bin", "https://cdn/original")
            val firstShared = cache.loadSharedOriginal("shared-file", "shared.bin", "https://cdn/shared")
            val secondShared = cache.loadSharedOriginal("shared-file", "shared.bin", "https://cdn/shared")

            assertEquals(firstShared, secondShared)
            assertEquals(5, loader.calls.get())
            assertEquals(5, cache.entryCount())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun failedAndCancelledLoadsLeaveNoIndexEntryOrTemporaryFile() = runTest {
        val root = Files.createTempDirectory("file-cache-failure-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val failureCache = cache(root, RecordingLoader { _, destination ->
                destination.writeBytes(byteArrayOf(1))
                throw IOException("partial response")
            }, scope)
            val failedKey = FileCacheKey.original(scope, "failed")
            assertTrue(runCatching { failureCache.load(failedKey, "failed.bin") }.exceptionOrNull() is IOException)
            assertTrue(failureCache.statistics().entryCount == 0)
            assertTrue(managedFiles(root).isEmpty())

            val started = CompletableDeferred<Unit>()
            val cancelCache = cache(root, RecordingLoader { _, _ ->
                started.complete(Unit)
                awaitCancellation()
            }, scope)
            val cancelled = async { cancelCache.load(FileCacheKey.original(scope, "cancelled"), "cancelled.bin") }
            started.await()
            cancelled.cancelAndJoin()

            assertEquals(0, cancelCache.entryCount())
            assertTrue(managedFiles(root).isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun missingOrPartialLoaderOutputNeverBecomesAnEntry() = runTest {
        val root = Files.createTempDirectory("file-cache-partial-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val cache = cache(root, RecordingLoader { _, destination -> destination.writeBytes(byteArrayOf()) }, scope)

            val error = runCatching { cache.load(FileCacheKey.original(scope, "empty"), "empty.bin") }
                .exceptionOrNull()

            assertTrue(error is IOException)
            assertEquals(0, cache.entryCount())
            assertTrue(managedFiles(root).isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun clearingDuringLoadCancelsItAndDoesNotLeaveAResult() = runTest {
        val root = Files.createTempDirectory("file-cache-clear-flight-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val started = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val cache = cache(root, RecordingLoader { _, destination ->
                started.complete(Unit)
                withContext(NonCancellable) {
                    release.await()
                    destination.parentFile?.mkdirs()
                    destination.writeBytes(byteArrayOf(7, 8))
                }
            }, scope)
            val request = async {
                runCatching { cache.load(FileCacheKey.original(scope, "in-flight"), "in-flight.bin") }
            }
            started.await()

            cache.clearAll()
            release.complete(Unit)
            assertTrue(request.await().exceptionOrNull() is CacheInvalidatedException)

            assertEquals(0, cache.entryCount())
            assertTrue(managedFiles(root).isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun changingScopeCancelsInFlightLoadAndPreventsItsCommit() = runTest {
        val root = Files.createTempDirectory("file-cache-scope-change-").toFile()
        try {
            var activeScope = CacheScope("server-a", "user")
            val started = CompletableDeferred<Unit>()
            val cache = cache(root, RecordingLoader { _, _ ->
                started.complete(Unit)
                awaitCancellation()
            }) { activeScope }
            val request = async {
                runCatching { cache.load(FileCacheKey.original(activeScope, "in-flight"), "in-flight.bin") }
            }
            started.await()

            activeScope = CacheScope("server-b", "user")
            cache.cancelInFlightLoads()
            assertTrue(request.await().exceptionOrNull() is CacheInvalidatedException)

            assertEquals(0, cache.entryCount())
            assertTrue(managedFiles(root).isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun startupReconciliationAdoptsLegacyOriginalsAndRemovesMissingRows() = runTest {
        val root = Files.createTempDirectory("file-cache-reconcile-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val legacy = File(root, "BarkCloudFiles/originals/legacy-id.pdf").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(9, 8, 7))
            }
            val diskIndex = DiskFileCacheIndex(File(root, "index.json"))
            val missingKey = FileCacheKey.original(scope, "missing")
            diskIndex.upsert(FileCacheEntry(missingKey, 12, 1, "missing-file.bin"))
            val cache = FileCacheService(
                baseDirectory = root,
                index = diskIndex,
                loader = RecordingLoader { _, _ -> error("cache hit expected") },
                settings = TestPolicy(),
                currentScope = { scope },
            )

            val adopted = cache.get(FileCacheKey.original(scope, "legacy-id"))

            assertEquals(legacy, adopted)
            assertNotNull(diskIndex.get(FileCacheKey.original(scope, "legacy-id").storageKey))
            assertNull(diskIndex.get(missingKey.storageKey))
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun startupDeletesOrphanEntryAndManagedFilesButAdoptsLegacyOriginalNames() = runTest {
        val root = Files.createTempDirectory("file-cache-orphan-cleanup-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val originals = File(root, "BarkCloudFiles/originals")
            val orphanEntry = File(originals, "entry-ZmlsZQ.0123456789abcdef.jpg").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(1, 2))
            }
            val legacy = File(originals, "legacy-name.pdf").apply { writeBytes(byteArrayOf(3, 4)) }
            val orphanPreview = File(root, "BarkCloudFiles/managed/previews/unindexed.jpg").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(5))
            }
            val orphanJpegView = File(root, "BarkCloudFiles/managed/jpeg-views/unindexed.jpg").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(6))
            }
            val cache = cache(root, RecordingLoader { _, _ -> error("cached files expected") }, scope)

            cache.statistics()

            assertFalse(orphanEntry.exists())
            assertFalse(orphanPreview.exists())
            assertFalse(orphanJpegView.exists())
            assertEquals(legacy, cache.get(FileCacheKey.original(scope, "legacy-name")))
            assertEquals(1, cache.entryCount())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun coilPreviewDirectoryIsMeasuredSeparatelyAndNotClearedByStorageService() = runTest {
        val root = Files.createTempDirectory("file-cache-coil-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val coilPreview = File(root, "BarkCloudFiles/previews/coil-entry.0").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(1, 2, 3, 4))
            }
            File(root, "BarkCloudFiles/previews/coil-entry.1").writeBytes(ByteArray(30))
            File(root, "BarkCloudFiles/previews/journal").writeBytes(ByteArray(100))
            val cache = cache(root, RecordingLoader { _, _ -> error("no download expected") }, scope)

            val stats = cache.statistics()
            cache.clearAll()

            assertEquals(0L, stats.totalSizeBytes)
            assertEquals(4L, stats.legacyPreviewSizeBytes)
            assertEquals(1, stats.legacyPreviewEntryCount)
            assertTrue(coilPreview.isFile)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun lruExpiryAndOversizedLastEntryFollowCachePolicy() = runTest {
        val root = Files.createTempDirectory("file-cache-policy-").toFile()
        try {
            val scope = CacheScope("server", "user")
            var now = 1_000_000L
            val policy = TestPolicy(maxCacheBytes = 5L, staleMaxAgeMillis = FileCacheSettings.DEFAULT_STALE_AGE_MILLIS)
            val cache = cache(root, RecordingLoader { request, destination ->
                destination.writeBytes(ByteArray(if (request.key.fileId == "large") 8 else 3))
            }, scope, policy, nowMillis = { now })

            val old = cache.load(FileCacheKey.original(scope, "old"), "old.bin")
            now += 1
            val newest = cache.load(FileCacheKey.original(scope, "newest"), "newest.bin")
            assertFalse(old.exists())
            assertTrue(newest.exists())
            assertEquals(3L, cache.totalSize())

            now += FileCacheSettings.DEFAULT_STALE_AGE_MILLIS + 1L
            assertEquals(1, cache.clearStale())
            assertFalse(newest.exists())

            now += 1
            val large = cache.load(FileCacheKey.original(scope, "large"), "large.bin")
            val stats = cache.statistics()
            assertTrue(large.exists())
            assertEquals(1, stats.entryCount)
            assertEquals(8L, stats.totalSizeBytes)
            assertTrue(stats.isOverLimit)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun explicitPinProtectsEntryUntilUnpinned() = runTest {
        val root = Files.createTempDirectory("file-cache-pin-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val policy = TestPolicy(maxCacheBytes = 3L)
            val cache = cache(root, RecordingLoader { _, destination -> destination.writeBytes(ByteArray(3)) }, scope, policy)
            val pinnedKey = FileCacheKey.original(scope, "pinned")
            val pinned = cache.load(pinnedKey, "pinned.bin")
            val pin = cache.pin(pinnedKey)
            assertNotNull(pin)
            cache.load(FileCacheKey.original(scope, "latest"), "latest.bin")

            assertTrue(pinned.exists())
            cache.unpin(requireNotNull(pin).pin)
            assertFalse(pinned.exists())
            assertEquals(1, cache.entryCount())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun cancelledOwnerCleansUpAfterStateMutexBecomesAvailable() = runTest {
        val root = Files.createTempDirectory("file-cache-owner-cancel-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val index = DiskFileCacheIndex(File(root, "index.json"))
            val started = CompletableDeferred<Unit>()
            val attempts = AtomicInteger()
            val cache = cache(
                root,
                RecordingLoader { _, destination ->
                    if (attempts.incrementAndGet() == 1) {
                        started.complete(Unit)
                        awaitCancellation()
                    }
                    destination.writeBytes(byteArrayOf(8, 9))
                },
                scope,
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
                index = index,
            )
            cache.statistics()
            val key = FileCacheKey.original(scope, "file")
            val owner = launch(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "file.bin") }
            started.await()

            val blockingKey = FileCacheKey.original(scope, "mutex-holder")
            index.blockingGetStorageKey = blockingKey.storageKey
            val holder = async(start = CoroutineStart.UNDISPATCHED) { cache.get(blockingKey) }
            index.getBlocked.await()
            owner.cancel()
            index.releaseGet.complete(Unit)
            holder.await()
            owner.join()

            val recovered = withTimeout(5_000) { cache.load(key, "file.bin") }
            assertEquals(byteArrayOf(8, 9).toList(), recovered.readBytes().toList())
            assertEquals(2, attempts.get())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun activeWaiterCompletesSingleLoadWhenOwnerIsCancelled() = runTest {
        val root = Files.createTempDirectory("file-cache-waiter-owner-cancel-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val started = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val loader = RecordingLoader { _, destination ->
                started.complete(Unit)
                release.await()
                destination.writeBytes(byteArrayOf(3, 4, 5))
            }
            val cache = cache(root, loader, scope, ioDispatcher = UnconfinedTestDispatcher(testScheduler))
            val key = FileCacheKey.original(scope, "shared")
            val owner = launch(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "shared.bin") }
            started.await()
            val waiter = async(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "shared.bin") }

            owner.cancelAndJoin()
            release.complete(Unit)

            assertEquals(byteArrayOf(3, 4, 5).toList(), waiter.await().readBytes().toList())
            assertEquals(1, loader.calls.get())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun activeWaiterTakesOverAFlightWhoseResultWasCancelled() = runTest {
        val root = Files.createTempDirectory("file-cache-waiter-retry-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val started = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val loaderCalls = AtomicInteger()
            val loader = RecordingLoader { _, destination ->
                if (loaderCalls.incrementAndGet() == 1) {
                    started.complete(Unit)
                    release.await()
                    throw CancellationException("loader owner stopped")
                }
                destination.writeBytes(byteArrayOf(6, 7))
            }
            val cache = cache(root, loader, scope, ioDispatcher = UnconfinedTestDispatcher(testScheduler))
            val key = FileCacheKey.original(scope, "retry")
            val owner = async(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "retry.bin") }
            started.await()
            val waiter = async(start = CoroutineStart.UNDISPATCHED) { cache.load(key, "retry.bin") }
            release.complete(Unit)

            val first = owner.await()
            assertEquals(first, waiter.await())
            assertEquals(2, loaderCalls.get())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun cancellingClearAllAlwaysReleasesTheClearGate() = runTest {
        val root = Files.createTempDirectory("file-cache-clear-cancel-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val index = DiskFileCacheIndex(File(root, "index.json"))
            val cache = cache(
                root,
                RecordingLoader { _, destination -> destination.writeBytes(byteArrayOf(1, 2)) },
                scope,
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
                index = index,
            )
            cache.load(FileCacheKey.original(scope, "before-clear"), "before.bin")
            index.blockClear = true
            val clearing = launch { cache.clearAll() }
            index.clearBlocked.await()

            clearing.cancel()
            clearing.join()
            index.blockClear = false

            val after = withTimeout(5_000) {
                cache.load(FileCacheKey.original(scope, "after-clear"), "after.bin")
            }
            assertTrue(after.isFile)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun cancelledPinnedActionReleasesItsPinAndLateUnpinCannotReleaseANewPin() = runTest {
        val root = Files.createTempDirectory("file-cache-pin-cancel-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val policy = TestPolicy(maxCacheBytes = 3L)
            val cache = cache(
                root,
                RecordingLoader { _, destination -> destination.writeBytes(ByteArray(3)) },
                scope,
                policy,
            )
            val key = FileCacheKey.original(scope, "pinned")
            val file = cache.load(key, "pinned.bin")
            val entered = CompletableDeferred<Unit>()
            val showing = launch {
                cache.withPinnedEntry(key) {
                    entered.complete(Unit)
                    awaitCancellation()
                }
            }
            entered.await()
            cache.load(FileCacheKey.original(scope, "newest"), "newest.bin")
            showing.cancelAndJoin()
            assertFalse(file.exists())

            cache.clearAll()
            cache.load(key, "pinned.bin")
            val oldPin = requireNotNull(cache.pin(key))
            cache.clearAll()
            cache.load(key, "pinned.bin")
            val newPin = requireNotNull(cache.pin(key))
            cache.unpin(oldPin.pin)
            val latest = cache.load(FileCacheKey.original(scope, "latest"), "latest.bin")
            assertTrue(newPin.file.exists())
            assertTrue(latest.exists())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun firstKnownAccountRebindsUnknownEntriesWithoutCancellingLoads() = runTest {
        val root = Files.createTempDirectory("file-cache-account-rebind-").toFile()
        try {
            var activeScope = CacheScope("server", CacheScope.UNKNOWN_ACCOUNT)
            val unknownScope = activeScope
            val legacy = File(root, "BarkCloudFiles/originals/legacy-vault.pdf").apply {
                parentFile?.mkdirs()
                writeBytes(byteArrayOf(7, 8))
            }
            val started = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val index = DiskFileCacheIndex(File(root, "index.json"))
            val cache = cache(
                root,
                RecordingLoader { request, destination ->
                    if (request.key.fileId == "in-flight") {
                        started.complete(Unit)
                        release.await()
                    }
                    destination.writeBytes(byteArrayOf(4, 5, 6))
                },
                policy = TestPolicy(),
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
                index = index,
            ) { activeScope }
            cache.statistics()
            val request = async { cache.load(FileCacheKey.original(unknownScope, "in-flight"), "photo.bin") }
            started.await()

            activeScope = CacheScope("server", "42")
            cache.accountIdentityChanged("server", null, "42")
            cache.rebindUnknownAccount("server", "42")
            release.complete(Unit)
            val loaded = withTimeout(5_000) { request.await() }

            val legacyKey = FileCacheKey.original(activeScope, "legacy-vault")
            val loadedKey = FileCacheKey.original(activeScope, "in-flight")
            assertTrue(loaded.isFile)
            assertEquals(legacy, cache.get(legacyKey))
            assertNotNull(index.get(legacyKey.storageKey))
            assertNotNull(index.get(loadedKey.storageKey))
            assertTrue(index.entriesInScope(unknownScope).isEmpty())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun changingFromOneKnownAccountToAnotherInvalidatesLoadsAsIOException() = runTest {
        val root = Files.createTempDirectory("file-cache-account-change-").toFile()
        try {
            var activeScope = CacheScope("server", "41")
            val started = CompletableDeferred<Unit>()
            val cache = cache(
                root,
                RecordingLoader { _, _ ->
                    started.complete(Unit)
                    awaitCancellation()
                },
                policy = TestPolicy(),
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
            ) { activeScope }
            val request = async { runCatching { cache.load(FileCacheKey.original(activeScope, "file"), "file.bin") } }
            started.await()

            activeScope = CacheScope("server", "42")
            cache.accountIdentityChanged("server", "41", "42")

            assertTrue(withTimeout(5_000) { request.await().exceptionOrNull() } is CacheInvalidatedException)
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun accessTimestampWritesAreThrottledWhileLruEvictionStaysOrdered() = runTest {
        val root = Files.createTempDirectory("file-cache-touch-throttle-").toFile()
        try {
            val scope = CacheScope("server", "user")
            var now = 1_000L
            val index = DiskFileCacheIndex(File(root, "index.json"))
            val cache = cache(
                root,
                RecordingLoader { _, destination -> destination.writeBytes(ByteArray(3)) },
                scope,
                TestPolicy(maxCacheBytes = 5L),
                nowMillis = { now },
                ioDispatcher = UnconfinedTestDispatcher(testScheduler),
                index = index,
            )
            val oldKey = FileCacheKey.original(scope, "old")
            val old = cache.load(oldKey, "old.bin")
            val initialWrites = index.upsertCalls
            now += 1_000
            cache.get(oldKey)
            assertEquals(initialWrites, index.upsertCalls)
            now += 30_000
            cache.get(oldKey)
            assertEquals(initialWrites + 1, index.upsertCalls)
            now += 1
            val newest = cache.load(FileCacheKey.original(scope, "newest"), "newest.bin")

            assertFalse(old.exists())
            assertTrue(newest.exists())
            assertEquals(3L, cache.totalSize())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun invalidatingOriginalDeletesItsIndexRowAndFile() = runTest {
        val root = Files.createTempDirectory("file-cache-invalidate-original-").toFile()
        try {
            val scope = CacheScope("server", "user")
            val cache = cache(root, RecordingLoader { _, destination -> destination.writeBytes(byteArrayOf(1, 2, 3)) }, scope)
            val key = FileCacheKey.original(scope, "broken")
            val file = cache.load(key, "broken.jpg")

            cache.invalidateOriginal("broken")

            assertFalse(file.exists())
            assertNull(cache.get(key))
            assertEquals(0, cache.entryCount())
        } finally {
            root.deleteRecursively()
        }
    }

    private fun cache(
        root: File,
        loader: FileCacheLoader,
        initialScope: CacheScope,
        policy: TestPolicy = TestPolicy(),
        nowMillis: () -> Long = { 100L },
        ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
        index: FileCacheIndex? = null,
    ) = cache(root, loader, policy, nowMillis, ioDispatcher, index) { initialScope }

    private fun cache(
        root: File,
        loader: FileCacheLoader,
        policy: TestPolicy = TestPolicy(),
        nowMillis: () -> Long = { 100L },
        ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
        index: FileCacheIndex? = null,
        scope: () -> CacheScope,
    ) = FileCacheService(
        baseDirectory = root,
        index = index ?: DiskFileCacheIndex(File(root, "index.json")),
        loader = loader,
        settings = policy,
        currentScope = scope,
        nowMillis = nowMillis,
        ioDispatcher = ioDispatcher,
    )

    private fun managedFiles(root: File): List<File> =
        File(root, "BarkCloudFiles").walkTopDown().filter { it.isFile }.toList()

    private class TestPolicy(
        override var maxCacheBytes: Long = 1024L * 1024L,
        override var staleMaxAgeMillis: Long = FileCacheSettings.DEFAULT_STALE_AGE_MILLIS,
        override var lastSweepAtMillis: Long = 0L,
    ) : FileCachePolicy

    private class RecordingLoader(
        private val action: suspend (FileCacheLoadRequest, File) -> Unit,
    ) : FileCacheLoader {
        val calls = AtomicInteger()

        override suspend fun load(request: FileCacheLoadRequest, destination: File) {
            calls.incrementAndGet()
            action(request, destination)
        }
    }

    private class DiskFileCacheIndex(private val file: File) : FileCacheIndex {
        private val mutex = Mutex()
        var upsertCalls = 0
        var blockingGetStorageKey: String? = null
        val getBlocked = CompletableDeferred<Unit>()
        val releaseGet = CompletableDeferred<Unit>()
        var blockClear = false
        val clearBlocked = CompletableDeferred<Unit>()
        val releaseClear = CompletableDeferred<Unit>()

        override suspend fun get(storageKey: String): FileCacheEntry? {
            if (storageKey == blockingGetStorageKey) {
                getBlocked.complete(Unit)
                releaseGet.await()
            }
            return mutex.withLock { readEntries()[storageKey] }
        }

        override suspend fun all(): List<FileCacheEntry> = mutex.withLock { readEntries().values.toList() }

        override suspend fun upsert(entry: FileCacheEntry) {
            mutex.withLock {
                upsertCalls++
                val entries = readEntries().toMutableMap().apply { put(entry.key.storageKey, entry) }
                writeEntries(entries)
            }
        }

        override suspend fun delete(storageKey: String) {
            mutex.withLock {
                val entries = readEntries().toMutableMap().apply { remove(storageKey) }
                writeEntries(entries)
            }
        }

        override suspend fun clear() {
            if (blockClear) {
                clearBlocked.complete(Unit)
                releaseClear.await()
            }
            mutex.withLock { writeEntries(emptyMap()) }
        }

        private fun readEntries(): Map<String, FileCacheEntry> {
            if (!file.isFile) return emptyMap()
            val array = JSONObject(file.readText()).optJSONArray("entries") ?: JSONArray()
            return buildMap {
                for (index in 0 until array.length()) {
                    val value = array.getJSONObject(index)
                    val key = FileCacheKey(
                        scope = CacheScope(value.getString("server"), value.getString("account")),
                        fileId = value.getString("fileId"),
                        variant = value.getString("variant"),
                        category = CacheCategory.valueOf(value.getString("category")),
                    )
                    put(
                        value.getString("key"),
                        FileCacheEntry(key, value.getLong("size"), value.getLong("lastUsed"), value.getString("fileName")),
                    )
                }
            }
        }

        private fun writeEntries(entries: Map<String, FileCacheEntry>) {
            file.parentFile?.mkdirs()
            val array = JSONArray()
            entries.forEach { (storageKey, entry) ->
                array.put(
                    JSONObject()
                        .put("key", storageKey)
                        .put("server", entry.key.scope.serverKey)
                        .put("account", entry.key.scope.accountId)
                        .put("fileId", entry.key.fileId)
                        .put("variant", entry.key.variant)
                        .put("category", entry.key.category.name)
                        .put("size", entry.sizeBytes)
                        .put("lastUsed", entry.lastUsedAt)
                        .put("fileName", entry.fileName),
                )
            }
            val temporary = File(file.parentFile, "${file.name}.tmp")
            temporary.writeText(JSONObject().put("entries", array).toString())
            Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING)
        }
    }
}
