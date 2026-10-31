package com.barkfluff.BarkCloud.data.update

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import io.mockk.every
import io.mockk.mockk
import java.io.File
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class AndroidUpdateRepositoryTest {
    @get:Rule val temporaryFolder = TemporaryFolder()

    @Test
    fun restoreMatchesOnlyAttemptSpecificDownloadAndRecoversMissingIdGap() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val attempt = attempt("v1.0.5", downloadId = null)
        store.currentAttempt = attempt
        gateway.add(attempt, id = 42, status = UpdateDownloadStatus.PENDING)
        val repository = repository(store, gateway)

        repository.reconcile()

        assertEquals(42L, store.currentAttempt?.downloadId)
        assertEquals(AndroidUpdatePhase.DOWNLOADING, repository.state.value.phase)
    }

    @Test
    fun invalidPersistedAttemptRemovesItsDownloadAndPartialFile() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val attempt = attempt("v1.0.5", downloadId = 42).copy(fileName = "unexpected.apk")
        store.currentAttempt = attempt
        gateway.add(attempt, id = 42, status = UpdateDownloadStatus.RUNNING)
        val partial = File(gateway.external, "updates/${attempt.fileName}").apply {
            parentFile!!.mkdirs()
            writeText("partial")
        }
        val repository = repository(store, gateway)

        repository.reconcile()

        assertNull(store.currentAttempt)
        assertTrue(gateway.removedIds.contains(42L))
        assertFalse(partial.exists())
        assertEquals(AndroidUpdatePhase.FAILED, repository.state.value.phase)
    }

    @Test
    fun missingDownloadManagerRowRestoresAsActionableCanceledState() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val attempt = attempt("v1.0.5", downloadId = 77)
        store.currentAttempt = attempt
        File(gateway.external, "updates/${attempt.fileName}").apply { parentFile!!.mkdirs(); writeText("partial") }
        val repository = repository(store, gateway)

        repository.reconcile()

        assertNull(store.currentAttempt)
        assertEquals(AndroidUpdatePhase.FAILED, repository.state.value.phase)
        assertFalse(File(gateway.external, "updates/${attempt.fileName}").exists())
        assertTrue(repository.state.value.message.orEmpty().contains("Запустите её ещё раз"))
    }

    @Test
    fun pausedDownloadRestoresProgressAndWaitingState() = runBlocking {
        val store = FakeStore()
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val attempt = attempt("v1.0.5", downloadId = 5)
        store.currentAttempt = attempt
        gateway.rows[5] = UpdateDownloadRecord(
            id = 5,
            title = AndroidDownloadManagerGateway.title(attempt.attemptId),
            status = UpdateDownloadStatus.PAUSED,
            reason = android.app.DownloadManager.PAUSED_WAITING_FOR_NETWORK,
            localUri = null,
            downloadedBytes = 64,
            totalBytes = 128,
        )
        val repository = repository(store, gateway)

        repository.reconcile()

        assertEquals(AndroidUpdatePhase.WAITING, repository.state.value.phase)
        assertEquals(64L, repository.state.value.downloadedBytes)
        assertEquals(128L, repository.state.value.totalBytes)
    }

    @Test
    fun cancelRemovesOwnDownloadAndPartialFile() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val repository = repository(store, gateway)
        repository.startDownload()
        val attempt = requireNotNull(store.currentAttempt)
        val partial = File(gateway.external, "updates/${attempt.fileName}")
        assertTrue(partial.exists())

        repository.cancelDownload()

        assertTrue(gateway.removedIds.contains(requireNotNull(attempt.downloadId)))
        assertFalse(partial.exists())
        assertNull(store.currentAttempt)
        assertEquals(AndroidUpdatePhase.AVAILABLE, repository.state.value.phase)
    }

    @Test
    fun concurrentStartOperationsDoNotEnqueueTwoDownloads() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val repository = repository(store, gateway)

        val outcomes = (1..2).map {
            async(Dispatchers.Default) { runCatching { repository.startDownload() }.isSuccess }
        }.awaitAll()

        assertEquals(1, outcomes.count { it })
        assertEquals(1, gateway.enqueueCount)
        assertEquals(1, gateway.rows.size)
        assertEquals(AndroidUpdatePhase.DOWNLOADING, repository.state.value.phase)
    }

    @Test
    fun cleanupWaitsForEnqueueAndKeepsFreshDownloadUnderRepositoryMutex() = runBlocking {
        val store = FakeStore().apply { cached = metadata("v1.0.5") }
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val enteredEnqueue = CountDownLatch(1)
        val allowEnqueueToFinish = CountDownLatch(1)
        gateway.beforeEnqueue = {
            enteredEnqueue.countDown()
            check(allowEnqueueToFinish.await(5, TimeUnit.SECONDS))
        }
        val repository = repository(store, gateway)
        val starting = async(Dispatchers.IO) { repository.startDownload() }
        assertTrue(enteredEnqueue.await(5, TimeUnit.SECONDS))
        val cleaning = async(Dispatchers.IO) { repository.cleanupAfterPackageReplacement() }
        allowEnqueueToFinish.countDown()
        awaitAll(starting, cleaning)

        assertNotNull(store.currentAttempt)
        assertEquals(1, gateway.rows.size)
        assertEquals(AndroidUpdatePhase.DOWNLOADING, repository.state.value.phase)
    }

    @Test
    fun completedDownloadIsVerifiedBeforeReadyAndCanceledWorkerIsObsolete() = runBlocking {
        val store = FakeStore()
        val gateway = FakeDownloads(temporaryFolder.newFolder("external"))
        val attempt = attempt("v1.0.5", downloadId = 9)
        store.currentAttempt = attempt
        val file = File(gateway.external, "updates/${attempt.fileName}").apply { parentFile!!.mkdirs(); writeText("apk") }
        gateway.rows[9] = UpdateDownloadRecord(
            9,
            AndroidDownloadManagerGateway.title(attempt.attemptId),
            UpdateDownloadStatus.SUCCESSFUL,
            0,
            file.toURI().toString(),
            128,
            128,
        )
        val verifier = FakeVerifier()
        val repository = repository(store, gateway, verifier = verifier)

        val result = repository.verifyAttempt(attempt.attemptId)

        assertEquals(UpdateVerificationResult.VERIFIED, result)
        assertTrue(requireNotNull(store.currentAttempt).ready)
        assertEquals(AndroidUpdatePhase.READY, repository.state.value.phase)
        assertEquals(1, verifier.calls)

        store.clearAttempt()
        assertEquals(UpdateVerificationResult.OBSOLETE, repository.verifyAttempt(attempt.attemptId))
    }

    @Test
    fun ttlCleanupRemovesOnlyTheStaleUpdaterRecordAndFile() = runBlocking {
        val store = FakeStore()
        val external = temporaryFolder.newFolder("external")
        val gateway = FakeDownloads(external)
        val attempt = attempt("v1.0.5", downloadId = 11, createdAt = NOW)
        store.currentAttempt = attempt
        val file = File(external, "updates/${attempt.fileName}").apply { parentFile!!.mkdirs(); writeText("partial") }
        gateway.add(attempt, id = 11, status = UpdateDownloadStatus.RUNNING)
        val repository = repository(store, gateway, now = NOW + AndroidUpdatePolicy.DOWNLOAD_TTL_MILLIS)

        repository.cleanupAfterPackageReplacement()

        assertNull(store.currentAttempt)
        assertTrue(gateway.removedIds.contains(11L))
        assertFalse(file.exists())
    }

    @Test
    fun successfulCheckInvalidatesReadyApkWhenLatestReleaseChanges() = runBlocking {
        val old = metadata("v1.0.5")
        val latest = metadata("v1.0.6")
        val store = FakeStore().apply { cached = old }
        val external = temporaryFolder.newFolder("external")
        val gateway = FakeDownloads(external)
        val attempt = attempt(old.tag, downloadId = 19, ready = true)
        store.currentAttempt = attempt
        val file = File(external, "updates/${attempt.fileName}").apply { parentFile!!.mkdirs(); writeText("verified") }
        gateway.rows[19] = UpdateDownloadRecord(
            19,
            AndroidDownloadManagerGateway.title(attempt.attemptId),
            UpdateDownloadStatus.SUCCESSFUL,
            0,
            file.toURI().toString(),
            old.apkSize,
            old.apkSize,
        )
        val fetcher = FakeFetcher { latest }
        val repository = repository(store, gateway, fetcher = fetcher)

        repository.checkForPage(force = true)

        assertNull(store.currentAttempt)
        assertFalse(file.exists())
        assertEquals(latest, store.cached)
        assertEquals(AndroidUpdatePhase.AVAILABLE, repository.state.value.phase)
    }

    @Test
    fun failedStartupAttemptIsPersistedAndNotRepeatedUntilTheNextDay() = runBlocking {
        val store = FakeStore().apply {
            cached = metadata("v1.0.5")
            promptDays["v1.0.5"] = "2999-01-01"
        }
        val fetcher = FakeFetcher { throw IllegalStateException("offline") }
        val repository = repository(store, FakeDownloads(temporaryFolder.newFolder("external")), fetcher = fetcher)

        assertNull(repository.checkForStartupPrompt())
        assertNull(repository.checkForStartupPrompt())

        assertEquals(1, fetcher.calls)
        assertEquals(NOW, store.lastStartupAttempt)
        assertEquals(NOW, store.lastAttempt)
    }

    @Test
    fun cachedStartupPromptDoesNotSkipTheDailyLatestReleaseCheck() = runBlocking {
        val store = FakeStore().apply {
            cached = metadata("v1.0.5")
            lastStartupAttempt = NOW - AndroidUpdatePolicy.STARTUP_INTERVAL_MILLIS
            promptDays["v1.0.5"] = "2000-01-01"
        }
        val latest = metadata("v1.0.6")
        val fetcher = FakeFetcher { latest }
        val repository = repository(store, FakeDownloads(temporaryFolder.newFolder("external")), fetcher = fetcher)

        assertEquals(latest, repository.checkForStartupPrompt())

        assertEquals(1, fetcher.calls)
        assertEquals(latest, store.cached)
        assertEquals(NOW, store.lastStartupAttempt)
    }

    @Test
    fun rateLimitIsPersistedAndManualChecksDoNotLoop() = runBlocking {
        val store = FakeStore()
        val fetcher = FakeFetcher { throw GitHubRateLimitException(NOW + 60_000, 429) }
        val repository = repository(store, FakeDownloads(temporaryFolder.newFolder("external")), fetcher = fetcher)

        repository.checkForPage(force = true)
        repository.checkForPage(force = true)

        assertEquals(1, fetcher.calls)
        assertEquals(NOW, store.lastAttempt)
        assertEquals(NOW + 60_000, store.rateLimitUntil)
        assertEquals(NOW + 60_000, repository.state.value.retryAtMillis)
    }

    private fun repository(
        store: FakeStore,
        gateway: FakeDownloads,
        fetcher: AndroidReleaseFetcher = FakeFetcher { metadata("v1.0.5") },
        verifier: FakeVerifier = FakeVerifier(),
        now: Long = NOW,
    ): AndroidUpdateRepository {
        val context = mockk<Context>()
        val packageManager = mockk<PackageManager>()
        val packageInfo = mockk<PackageInfo>()
        every { context.applicationContext } returns context
        every { context.packageName } returns APP_ID
        every { context.getExternalFilesDir(null) } returns gateway.external
        every { context.packageManager } returns packageManager
        every { packageManager.getPackageInfo(any<String>(), any<Int>()) } returns packageInfo
        every { packageInfo.longVersionCode } returns 2L
        return AndroidUpdateRepository(
            context = context,
            preferences = store,
            releaseClient = fetcher,
            downloads = gateway,
            apkVerifier = verifier,
            clock = { now },
            verificationScheduler = VerificationWorkScheduler { },
        )
    }

    private fun attempt(
        tag: String,
        downloadId: Long?,
        ready: Boolean = false,
        createdAt: Long = NOW,
    ): UpdateDownloadAttempt {
        val attemptId = UUID.randomUUID().toString()
        return UpdateDownloadAttempt(
            attemptId = attemptId,
            metadata = metadata(tag),
            createdAtMillis = createdAt,
            fileName = "$tag-$attemptId.apk",
            downloadId = downloadId,
            ready = ready,
        )
    }

    private fun metadata(tag: String) = AndroidReleaseMetadata(
        tag = tag,
        versionName = tag.removePrefix("v"),
        versionCode = AndroidReleaseParser.versionCode(tag),
        apkFileName = "BarkCloud.Android-$tag.apk",
        apkSize = 128,
        sha256 = "ab".repeat(32),
        notes = "",
    )

    private class FakeStore : AndroidUpdateStore {
        var cached: AndroidReleaseMetadata? = null
        var currentAttempt: UpdateDownloadAttempt? = null
        var unavailableTag: String? = null
        var lastAttempt = 0L
        var lastStartupAttempt = 0L
        var rateLimitUntil = 0L
        val promptDays = mutableMapOf<String, String>()

        override fun cachedRelease() = cached
        override fun cacheRelease(metadata: AndroidReleaseMetadata) {
            cached = metadata
            unavailableTag = null
            rateLimitUntil = 0
        }
        override fun markMetadataUnavailable(tag: String?) {
            unavailableTag = tag ?: "unknown"
        }
        override fun metadataUnavailableTag() = unavailableTag
        override fun lastAttemptMillis() = lastAttempt
        override fun lastStartupAttemptMillis() = lastStartupAttempt
        override fun rateLimitUntilMillis() = rateLimitUntil
        override fun recordAttempt(nowMillis: Long, startup: Boolean) {
            lastAttempt = nowMillis
            if (startup) lastStartupAttempt = nowMillis
        }
        override fun recordRateLimit(untilMillis: Long) {
            rateLimitUntil = untilMillis
        }
        override fun promptedDay(tag: String) = promptDays[tag]
        override fun markPromptShown(tag: String, day: String) {
            promptDays[tag] = day
        }
        override fun attempt() = currentAttempt
        override fun saveAttempt(attempt: UpdateDownloadAttempt) {
            currentAttempt = attempt
        }
        override fun clearAttempt() {
            currentAttempt = null
        }
    }

    private class FakeDownloads(val external: File) : UpdateDownloadGateway {
        val rows = linkedMapOf<Long, UpdateDownloadRecord>()
        val removedIds = mutableListOf<Long>()
        var enqueueCount = 0
        var beforeEnqueue: (() -> Unit)? = null
        private var nextId = 1L

        override fun enqueue(attemptId: String, metadata: AndroidReleaseMetadata, fileName: String): Long {
            beforeEnqueue?.invoke()
            enqueueCount++
            val id = nextId++
            val file = File(external, "updates/$fileName").apply { parentFile!!.mkdirs(); createNewFile() }
            rows[id] = UpdateDownloadRecord(
                id,
                AndroidDownloadManagerGateway.title(attemptId),
                UpdateDownloadStatus.PENDING,
                0,
                file.toURI().toString(),
                0,
                metadata.apkSize,
            )
            return id
        }

        override fun get(id: Long) = rows[id]
        override fun rowsForAttempt(attemptId: String) = rows.values
            .filter { it.title == AndroidDownloadManagerGateway.title(attemptId) }
        override fun managedRows() = rows.values.toList()
        override fun remove(id: Long) {
            removedIds += id
            rows.remove(id)
        }

        fun add(attempt: UpdateDownloadAttempt, id: Long, status: UpdateDownloadStatus) {
            val file = File(external, "updates/${attempt.fileName}")
            rows[id] = UpdateDownloadRecord(
                id,
                AndroidDownloadManagerGateway.title(attempt.attemptId),
                status,
                0,
                file.toURI().toString(),
                0,
                attempt.metadata.apkSize,
            )
        }
    }

    private class FakeVerifier : UpdateApkVerifier {
        var calls = 0
        override suspend fun verify(file: File, metadata: AndroidReleaseMetadata) {
            calls++
        }
    }

    private class FakeFetcher(private val result: suspend () -> AndroidReleaseMetadata) : AndroidReleaseFetcher {
        var calls = 0
        override suspend fun fetchLatest(): AndroidReleaseMetadata {
            calls++
            return result()
        }
    }

    private companion object {
        const val APP_ID = "com.barkfluff.BarkCloud"
        const val NOW = 1_000_000_000_000L
    }
}
