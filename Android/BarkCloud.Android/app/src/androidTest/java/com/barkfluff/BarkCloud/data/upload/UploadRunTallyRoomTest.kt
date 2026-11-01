package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class UploadRunTallyRoomTest {
    private lateinit var database: BarkCloudDatabase

    @Before
    fun setUp() {
        val context: Context = InstrumentationRegistry.getInstrumentation().targetContext
        database = Room.inMemoryDatabaseBuilder(context, BarkCloudDatabase::class.java)
            .allowMainThreadQueries()
            .build()
    }

    @After
    fun tearDown() {
        database.close()
    }

    @Test
    fun countsOnlyThisRunsCompletionsAndEligibleActiveJobs() = runBlocking {
        val dao = database.uploadDao()
        listOf(
            job("old-completed", UploadPhase.COMPLETED, completedAt = RUN_START - 1),
            job("done-now", UploadPhase.COMPLETED, completedAt = RUN_START + 1),
            job("old-failed", UploadPhase.FAILED),
            job("run-failed", UploadPhase.FAILED),
            job("queued", UploadPhase.QUEUED),
            job("attaching", UploadPhase.ATTACHING),
            job("paused-backup", UploadPhase.PAUSED, UploadSource.BACKUP),
            job("cancelled", UploadPhase.CANCELLED),
            job("backup-queued", UploadPhase.QUEUED, UploadSource.BACKUP),
            job("backup-failed", UploadPhase.FAILED, UploadSource.BACKUP),
        ).forEach { dao.insert(it) }

        val withBackup = dao.runTally(RUN_START, includeBackup = true, failedIds = listOf("run-failed", "backup-failed"))
        assertEquals(UploadRunTally(done = 1, remaining = 3, failed = 2), withBackup)

        // Политика сети убирает backup из remaining, но ошибка прохода остаётся в failed.
        val withoutBackup = dao.runTally(RUN_START, includeBackup = false, failedIds = listOf("run-failed", "backup-failed"))
        assertEquals(UploadRunTally(done = 1, remaining = 2, failed = 2), withoutBackup)
    }

    @Test
    fun recordedBackupFailureStaysCountedWhileNetworkBlocksBackup() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("done-now", UploadPhase.COMPLETED, completedAt = RUN_START + 1))
        dao.insert(job("backup-failed", UploadPhase.FAILED, UploadSource.BACKUP))

        // Платная сеть: backup не eligible, но упавший в этом проходе backup не должен выглядеть успехом.
        val blocked = dao.runTally(RUN_START, includeBackup = false, failedIds = listOf("backup-failed"))

        assertEquals(UploadRunTally(done = 1, remaining = 0, failed = 1), blocked)
        assertFalse(blocked.isGenuineSuccess)
    }

    @Test
    fun cancellingRecordedBackupFailureRemovesItFromCount() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("done-now", UploadPhase.COMPLETED, completedAt = RUN_START + 1))
        dao.insert(job("backup-failed", UploadPhase.FAILED, UploadSource.BACKUP))
        assertEquals(1, dao.cancel("backup-failed"))

        val tally = dao.runTally(RUN_START, includeBackup = false, failedIds = listOf("backup-failed"))

        assertEquals(UploadRunTally(done = 1, remaining = 0, failed = 0), tally)
        assertTrue(tally.isGenuineSuccess)
    }

    @Test
    fun existingFailureCountsOnlyWhenItFailsDuringThisRun() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("old-failed", UploadPhase.FAILED))

        assertEquals(UploadRunTally(0, 0, 0), dao.runTally(RUN_START, true, failedIds = emptyList()))
        assertEquals(UploadRunTally(0, 0, 1), dao.runTally(RUN_START, true, failedIds = listOf("old-failed")))
    }

    @Test
    fun backupCompletionsCountAsDoneEvenWhenBackupIsBlockedNow() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("backup-done", UploadPhase.COMPLETED, UploadSource.BACKUP, completedAt = RUN_START + 5))

        assertEquals(UploadRunTally(1, 0, 0), dao.runTally(RUN_START, includeBackup = false, failedIds = emptyList()))
    }

    private fun job(
        id: String,
        phase: UploadPhase,
        source: UploadSource = UploadSource.MANUAL,
        completedAt: Long? = null,
    ) = UploadJob(
        id = id,
        source = source,
        stagedFilePath = null,
        mediaKey = if (source == UploadSource.BACKUP) "image:$id" else null,
        mediaHash = null,
        fileName = "$id.jpg",
        mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = null,
        phase = phase,
        idempotencyKey = null,
        sessionId = null,
        partSize = 0L,
        preparedFileId = null,
        bytesTotal = 10L,
        bytesSent = 0L,
        errorMessage = null,
        createdAtMillis = 1L,
        completedAtMillis = completedAt,
    )

    private companion object {
        const val RUN_START = 1_000L
    }
}
