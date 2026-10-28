package com.barkfluff.BarkCloud.ui.upload

import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource
import org.junit.Assert.assertEquals
import org.junit.Test

class UploadQueueUiStateTest {
    @Test fun unfinishedJobsRemainVisibleRegardlessOfAge() {
        val phases = listOf(UploadPhase.QUEUED, UploadPhase.UPLOADING, UploadPhase.PAUSED,
            UploadPhase.FAILED, UploadPhase.UPLOADED_NOT_ATTACHED)
        val jobs = (phases + UploadPhase.COMPLETED + UploadPhase.CANCELLED).map(::job)

        assertEquals(phases, UploadQueueUiState(jobs).pendingJobs.map { it.phase })
    }

    @Test fun completedJobDisappearsOnNextStateUpdate() {
        val uploading = job(UploadPhase.UPLOADING)
        val state = UploadQueueUiState(listOf(uploading))
        assertEquals(1, state.pendingJobs.size)
        assertEquals(emptyList<UploadJob>(), state.copy(jobs = listOf(uploading.copy(phase = UploadPhase.COMPLETED))).pendingJobs)
    }

    private fun job(phase: UploadPhase) = UploadJob(
        id = phase.name, source = UploadSource.BACKUP, stagedFilePath = null,
        mediaKey = null, mediaHash = null, fileName = "photo.jpg", mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND, directoryId = null, albumId = null,
        phase = phase, idempotencyKey = null, sessionId = null, partSize = 0,
        preparedFileId = null, bytesTotal = 100, bytesSent = 50, errorMessage = null,
        createdAtMillis = 1, completedAtMillis = null,
    )
}
