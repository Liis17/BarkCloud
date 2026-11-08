package com.barkfluff.BarkCloud.ui.upload

import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadQueueUiStateTest {
    @Test fun unfinishedJobsAreGroupedAndTerminalPhasesAreHidden() {
        val jobs = listOf(
            job(UploadPhase.QUEUED, id = "queued", createdAt = 0),
            job(UploadPhase.HASHING, id = "hashing", createdAt = 1),
            job(UploadPhase.CREATING_SESSION, id = "session", createdAt = 2),
            job(UploadPhase.UPLOADING, id = "uploading", createdAt = 3),
            job(UploadPhase.COMPLETING, id = "completing", createdAt = 4),
            job(UploadPhase.PROCESSING, id = "processing", createdAt = 5),
            job(UploadPhase.ATTACHING, id = "attaching", createdAt = 6),
            job(UploadPhase.PAUSED, id = "paused", createdAt = 7),
            job(UploadPhase.FAILED, id = "failed", createdAt = 8),
            job(UploadPhase.UPLOADED_NOT_ATTACHED, id = "detached", createdAt = 9),
            job(UploadPhase.UPLOADED, id = "legacy", createdAt = 10),
            job(UploadPhase.COMPLETED, id = "completed", createdAt = 0),
            job(UploadPhase.CANCELLED, id = "cancelled", createdAt = 0),
        )

        assertEquals(
            listOf("hashing", "session", "uploading", "completing", "processing", "attaching", "queued", "paused", "failed", "detached", "legacy"),
            UploadQueueUiState(jobs).pendingJobs.map { it.id },
        )
    }

    @Test fun jobsWithinGroupsUseCreationTimeThenId() {
        val state = UploadQueueUiState(
            listOf(
                job(UploadPhase.UPLOADING, id = "active-z", createdAt = 10),
                job(UploadPhase.HASHING, id = "active-b", createdAt = 5),
                job(UploadPhase.CREATING_SESSION, id = "active-a", createdAt = 5),
                job(UploadPhase.QUEUED, id = "queued-b", createdAt = 5),
                job(UploadPhase.QUEUED, id = "queued-a", createdAt = 5),
                job(UploadPhase.FAILED, id = "inactive-b", createdAt = 2),
                job(UploadPhase.PAUSED, id = "inactive-a", createdAt = 2),
            ),
        )

        assertEquals(
            listOf("active-a", "active-b", "active-z", "queued-a", "queued-b", "inactive-a", "inactive-b"),
            state.pendingJobs.map { it.id },
        )
    }

    @Test fun pendingErrorCountIncludesOldUnfinishedFailures() {
        val state = UploadQueueUiState(
            listOf(
                job(UploadPhase.FAILED, id = "old-failure", createdAt = 1),
                job(UploadPhase.UPLOADED_NOT_ATTACHED, id = "old-attachment", createdAt = 2),
                job(UploadPhase.COMPLETED, id = "old-completed", createdAt = 3),
            ),
        )

        assertEquals(2, state.pendingErrorCount)
        assertEquals(0, state.failed)
    }

    @Test fun completedJobDisappearsOnNextStateUpdate() {
        val uploading = job(UploadPhase.UPLOADING)
        val state = UploadQueueUiState(listOf(uploading))
        assertEquals(1, state.pendingJobs.size)
        assertEquals(emptyList<UploadJob>(), state.copy(jobs = listOf(uploading.copy(phase = UploadPhase.COMPLETED))).pendingJobs)
    }

    @Test fun ringStaysVisibleForLongRunningBackupCreatedBeforeWindow() {
        val now = System.currentTimeMillis()
        val state = UploadQueueUiState(
            listOf(
                job(UploadPhase.UPLOADING, id = "old-active", createdAt = 1),
                job(UploadPhase.COMPLETED, id = "old-done", createdAt = 1).copy(completedAtMillis = now),
                job(UploadPhase.COMPLETED, id = "stale-done", createdAt = 1).copy(completedAtMillis = 1),
            ),
            workerRunning = true,
        )

        assertTrue(state.isActive)
        assertEquals(2, state.total)
        assertEquals(1, state.completed)
        assertEquals(0.75f, state.progress, 0.001f)
    }

    @Test fun uploadingJobWithoutRunningWorkerDoesNotShowRing() {
        // Backup на платной сети при WIFI_ONLY или в backoff: фаза UPLOADING осталась, воркер не работает.
        val state = UploadQueueUiState(listOf(job(UploadPhase.UPLOADING)), workerRunning = false)

        assertFalse(state.isActive)
        assertEquals("UPLOADING", state.current?.id)
    }

    private fun job(phase: UploadPhase, id: String = phase.name, createdAt: Long = 1) = UploadJob(
        id = id, source = UploadSource.BACKUP, stagedFilePath = null,
        mediaKey = null, mediaHash = null, fileName = "photo.jpg", mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND, directoryId = null, albumId = null,
        phase = phase, idempotencyKey = null, sessionId = null, partSize = 0,
        preparedFileId = null, bytesTotal = 100, bytesSent = 50, errorMessage = null,
        createdAtMillis = createdAt, completedAtMillis = null,
    )
}
