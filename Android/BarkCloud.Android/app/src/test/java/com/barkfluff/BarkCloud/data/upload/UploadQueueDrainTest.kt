package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.delay
import kotlinx.coroutines.test.runTest
import io.grpc.Metadata
import io.grpc.Status
import com.barkfluff.BarkCloud.net.UploadPartHttpException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.util.concurrent.atomic.AtomicInteger

class UploadQueueDrainTest {
    @Test
    fun keysetDrainProcessesLargeQueueWithAtMostFourWorkers() = runTest {
        val source = FakeQueue((1..247).map { job(it) }.toMutableList())
        val active = AtomicInteger()
        val maximum = AtomicInteger()
        val processed = mutableSetOf<String>()

        UploadQueueDrain.run(source, pageSize = 4, shouldStop = { false }) { item ->
            val now = active.incrementAndGet()
            maximum.updateAndGet { maxOf(it, now) }
            delay(1)
            processed += item.id
            active.decrementAndGet()
            source.complete(item.id)
        }

        assertEquals(247, processed.size)
        assertTrue(maximum.get() <= 4)
        assertEquals(0, source.remainingActive())
    }

    @Test
    fun sharedFailureStopsClaimingLaterPagesAndLeavesTailQueued() = runTest {
        val quotaTrailers = Metadata().apply {
            put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), UploadErrorCodes.QUOTA_EXCEEDED)
        }
        val errors = listOf(
            UploadPartHttpException(503, "server unavailable"),
            Status.UNAVAILABLE.asRuntimeException(),
            Status.RESOURCE_EXHAUSTED.asRuntimeException(quotaTrailers),
            IOException("No space left on device"),
            SecurityException("Media permission revoked"),
        )

        for (error in errors) {
            val source = FakeQueue((1..16).map { job(it).copy(source = UploadSource.BACKUP) }.toMutableList())
            val disposition = UploadFailurePolicy.classify(error, UploadSource.BACKUP)
            var stop = false
            val attempted = mutableSetOf<String>()

            UploadQueueDrain.run(source, pageSize = 4, shouldStop = { stop }) { item ->
                attempted += item.id
                if (item.id == "0001" && disposition != UploadFailureDisposition.ITEM_TERMINAL) stop = true
            }

            assertTrue(attempted.size <= 4)
            assertTrue(source.remainingActive() >= 12)
            assertFalse(source.items.filter { it.id !in attempted }.any { it.phase != UploadPhase.QUEUED })
        }
    }

    @Test
    fun unreadableSingleMediaIsTerminalWithoutBlockingTheQueueTail() = runTest {
        val source = FakeQueue((1..16).map { job(it) }.toMutableList())
        val disposition = UploadFailurePolicy.classify(BackupSourceReadException(IOException("unreadable")), UploadSource.BACKUP)
        val attempted = mutableSetOf<String>()

        UploadQueueDrain.run(source, pageSize = 4, shouldStop = { false }) { item ->
            attempted += item.id
            if (item.id == "0001") assertEquals(UploadFailureDisposition.ITEM_TERMINAL, disposition)
        }

        assertEquals(16, attempted.size)
    }

    @Test
    fun workAddedAsCurrentRunFinishesSchedulesAContinuationOnlyWhenUnblocked() {
        assertTrue(shouldScheduleUploadContinuation(stopClaiming = false, activeJobs = 1, stagedBackups = 2, maxStagedBackups = 4))
        assertFalse(shouldScheduleUploadContinuation(stopClaiming = false, activeJobs = 0, stagedBackups = 0, maxStagedBackups = 4))
        assertFalse(shouldScheduleUploadContinuation(stopClaiming = true, activeJobs = 3, stagedBackups = 0, maxStagedBackups = 4))
        assertFalse(shouldScheduleUploadContinuation(stopClaiming = false, activeJobs = 2, stagedBackups = 4, maxStagedBackups = 4))
    }

    @Test
    fun stagedCopiesAreReservedAcrossRetriesWithoutExceedingFour() {
        val budget = BackupStagingBudget(initialCount = 2)
        assertTrue(budget.reserve())
        assertTrue(budget.reserve())
        assertFalse(budget.reserve())
        assertEquals(4, budget.heldCount())
        budget.release()
        assertTrue(budget.reserve())
        assertEquals(4, budget.heldCount())
    }

    @Test
    fun attachOnlyBackupRetryDoesNotRequireSourceStaging() {
        val job = job(1).copy(
            source = UploadSource.BACKUP,
            stagedFilePath = null,
            preparedFileId = "ready-file",
            phase = UploadPhase.UPLOADED_NOT_ATTACHED,
        )

        assertTrue(job.isAttachOnlyPhase())
        assertFalse(job.needsBackupStaging())
    }

    @Test
    fun retryWithExistingSessionResumesTransferUntilServerReportsReady() {
        val failed = job(1).copy(
            source = UploadSource.MANUAL,
            phase = UploadPhase.FAILED,
            sessionId = "session",
            preparedFileId = "file-id",
        )

        assertEquals(UploadPhase.UPLOADING, failed.retryPhase())
        assertEquals(
            UploadPhase.ATTACHING,
            failed.copy(phase = UploadPhase.UPLOADED_NOT_ATTACHED).retryPhase(),
        )
    }

    private fun job(number: Int) = UploadJob(
        id = number.toString().padStart(4, '0'),
        source = UploadSource.MANUAL,
        stagedFilePath = "file",
        mediaKey = null,
        mediaHash = "hash",
        fileName = "file-$number",
        mimeType = "application/octet-stream",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = null,
        phase = UploadPhase.QUEUED,
        idempotencyKey = null,
        sessionId = null,
        partSize = 0,
        preparedFileId = null,
        bytesTotal = 1,
        bytesSent = 0,
        errorMessage = null,
        createdAtMillis = number.toLong(),
        completedAtMillis = null,
    )

    private class FakeQueue(val items: MutableList<UploadJob>) : ActiveUploadPageSource {
        override suspend fun activePage(afterId: String?, limit: Int): List<UploadJob> = items
            .asSequence()
            .filter { it.phase in setOf(UploadPhase.QUEUED, UploadPhase.UPLOADING) }
            .filter { afterId == null || it.id > afterId }
            .sortedBy { it.id }
            .take(limit)
            .toList()

        fun remainingActive(): Int = items.count { it.phase in setOf(UploadPhase.QUEUED, UploadPhase.UPLOADING) }

        fun complete(id: String) {
            val index = items.indexOfFirst { it.id == id }
            if (index >= 0) items[index] = items[index].copy(phase = UploadPhase.COMPLETED)
        }
    }
}
