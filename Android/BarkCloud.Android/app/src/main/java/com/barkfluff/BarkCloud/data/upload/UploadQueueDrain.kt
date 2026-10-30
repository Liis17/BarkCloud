package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch

internal interface ActiveUploadPageSource {
    suspend fun activePage(afterId: String?, limit: Int): List<UploadJob>
}

/** Reads stable keyset pages and never holds more than one bounded page of jobs in memory. */
internal object UploadQueueDrain {
    suspend fun run(
        source: ActiveUploadPageSource,
        pageSize: Int,
        shouldStop: () -> Boolean,
        process: suspend (UploadJob) -> Unit,
    ) {
        var afterId: String? = null
        while (!shouldStop()) {
            val page = source.activePage(afterId, pageSize)
            if (page.isEmpty()) break
            afterId = page.last().id
            coroutineScope {
                page.forEach { job -> launch { process(job) } }
            }
        }
    }
}

internal fun shouldScheduleUploadContinuation(
    stopClaiming: Boolean,
    activeJobs: Int,
    stagedBackups: Int,
    maxStagedBackups: Int,
): Boolean = !stopClaiming && activeJobs > 0 && stagedBackups < maxStagedBackups

internal fun UploadJob.isAttachOnlyPhase(): Boolean =
    phase == UploadPhase.ATTACHING || phase == UploadPhase.UPLOADED_NOT_ATTACHED

internal fun UploadJob.needsBackupStaging(): Boolean = source == UploadSource.BACKUP && !isAttachOnlyPhase()

internal fun UploadJob.retryPhase(): UploadPhase = when {
    phase == UploadPhase.UPLOADED_NOT_ATTACHED -> UploadPhase.ATTACHING
    sessionId != null -> UploadPhase.UPLOADING
    else -> UploadPhase.QUEUED
}
