package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import android.net.Uri
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.FileInputStream
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

@RunWith(AndroidJUnit4::class)
class UploadQueueStoreRoomRegressionTest {
    private lateinit var context: Context
    private lateinit var database: BarkCloudDatabase
    private lateinit var queueDirectory: File
    private var previousMigrationMarker: Boolean? = null

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().context
        val migrationPrefs = context.getSharedPreferences("barkcloud_upload2", Context.MODE_PRIVATE)
        previousMigrationMarker = migrationPrefs.getBoolean("migration.v1", false)
        migrationPrefs.edit().putBoolean("migration.v1", true).commit()
        database = Room.inMemoryDatabaseBuilder(context, BarkCloudDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        queueDirectory = File(context.cacheDir, "upload-queue-${UUID.randomUUID()}").apply { mkdirs() }
    }

    @After
    fun tearDown() {
        database.close()
        queueDirectory.deleteRecursively()
        val migrationPrefs = context.getSharedPreferences("barkcloud_upload2", Context.MODE_PRIVATE)
        val previous = previousMigrationMarker
        if (previous == true) migrationPrefs.edit().putBoolean("migration.v1", true).commit()
        else migrationPrefs.edit().remove("migration.v1").commit()
    }

    @Test
    fun quotaFailuresAndPausedStagesReleaseBeforeRealQueueDrain() = runBlocking {
        val tailMedia = (10L..29L).map { media(it, byteArrayOf(it.toByte(), 1, 2, 3)) }
        val queue = queue(FakeBackupMediaAccess(tailMedia.associateBy { it.mediaKey }))
        val dao = database.uploadDao()
        val quotaJobs = (1..4).map { backupJob(it, UploadPhase.UPLOADING, stagedFile(it)) }
        val pausedJobs = (5..6).map { backupJob(it, UploadPhase.PAUSED, stagedFile(it)) }
        (quotaJobs + pausedJobs).forEach { job ->
            File(requireNotNull(job.stagedFilePath)).apply { parentFile?.mkdirs(); writeText("staged:${job.id}") }
            dao.insert(job)
        }
        quotaJobs.forEach { assertEquals(1, queue.failAfterQuota(it.id, "quota exhausted")) }
        val inactive = quotaJobs.map { it.copy(phase = UploadPhase.FAILED, errorMessage = "quota exhausted") } + pausedJobs
        tailMedia.forEach { assertTrue(queue.enqueueBackupIfAbsent(it)) }

        queue.releaseExcessBackupStages(maximum = 4)

        assertEquals(0, queue.stagedBackupCount())
        inactive.forEach { previous ->
            val row = requireNotNull(queue.byId(previous.id))
            assertEquals(previous.phase, row.phase)
            assertNull(row.stagedFilePath)
            assertEquals(previous.mediaHash, row.mediaHash)
            assertEquals(previous.idempotencyKey, row.idempotencyKey)
            assertEquals(previous.sessionId, row.sessionId)
            assertFalse(File(requireNotNull(previous.stagedFilePath)).exists())
        }

        val budget = BackupStagingBudget(queue.stagedBackupCount())
        val active = AtomicInteger()
        val maximum = AtomicInteger()
        val processed = AtomicInteger()
        UploadQueueDrain.run(queue, pageSize = 4, shouldStop = { false }) { job ->
            val inFlight = active.incrementAndGet()
            maximum.updateAndGet { maxOf(it, inFlight) }
            var reservedStage = false
            try {
                if (job.stagedFilePath == null) {
                    assertTrue("released inactive copies must leave room for queued media", budget.reserve())
                    reservedStage = true
                }
                val prepared = requireNotNull(queue.prepareBackup(job.id, database.mediaCloudStateDao()) { })
                val stage = File(requireNotNull(prepared.job.stagedFilePath))
                assertTrue(stage.exists())
                assertTrue(queue.stagedBackupCount() <= 4)
                queue.complete(prepared.job)
                assertFalse(stage.exists())
                processed.incrementAndGet()
            } finally {
                if (reservedStage && queue.byId(job.id)?.stagedFilePath == null) budget.release()
                active.decrementAndGet()
            }
        }

        assertEquals(20, processed.get())
        assertEquals(0, queue.activeCount())
        assertTrue(maximum.get() <= 4)
    }

    @Test
    fun releasingExcessNeverDeletesAFileOwnedByAnActiveTransfer() = runBlocking {
        val queue = queue()
        val job = backupJob(40, UploadPhase.QUEUED, stagedFile(40))
        val file = File(requireNotNull(job.stagedFilePath)).apply { writeText("in-flight") }
        database.uploadDao().insert(job)
        val staleCandidate = database.uploadDao().releasableBackupStagePage(limit = 1).single()
        queue.setPhase(job.id, UploadPhase.UPLOADING)

        assertEquals(0, database.uploadDao().releaseBackupStage(job.id, file.absolutePath, staleCandidate.phase))

        queue.releaseExcessBackupStages(maximum = 0)

        val current = requireNotNull(queue.byId(job.id))
        assertEquals(UploadPhase.UPLOADING, current.phase)
        assertEquals(file.absolutePath, current.stagedFilePath)
        assertTrue(file.exists())
    }

    @Test
    fun initializeDeletesOnlyUntrackedBackupPartsAndStages() = runBlocking {
        val queue = queue()
        val dao = database.uploadDao()
        val tracked = backupJob(50, UploadPhase.PAUSED, stagedFile(50))
        val untracked = backupJob(51, UploadPhase.QUEUED, null)
        dao.insert(tracked)
        dao.insert(untracked)

        val trackedFile = File(requireNotNull(tracked.stagedFilePath)).apply { writeText("tracked") }
        val untrackedStage = File(stagedFile(51)).apply { writeText("orphan stage") }
        val untrackedPart = File(queueDirectory, "backup-${untracked.id}.part").apply { writeText("orphan part") }
        val manualFile = File(queueDirectory, "manual-job-photo.jpg").apply { writeText("manual source") }
        val shareFile = File(queueDirectory, "share-job-video.mp4").apply { writeText("share source") }
        dao.update(tracked.copy(bytesTotal = trackedFile.length()))
        listOf(UploadSource.MANUAL to manualFile, UploadSource.SHARE to shareFile).forEachIndexed { index, (source, file) ->
            dao.insert(
                UploadJob(
                    id = "${source.name.lowercase()}-job",
                    source = source,
                    stagedFilePath = file.absolutePath,
                    mediaKey = null,
                    mediaHash = null,
                    fileName = file.name,
                    mimeType = null,
                    destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                    directoryId = null,
                    albumId = null,
                    phase = UploadPhase.QUEUED,
                    idempotencyKey = "${source.name.lowercase()}-idempotency",
                    sessionId = null,
                    partSize = 0,
                    preparedFileId = null,
                    bytesTotal = file.length(),
                    bytesSent = 0,
                    errorMessage = null,
                    createdAtMillis = index.toLong(),
                    completedAtMillis = null,
                ),
            )
        }

        queue.initialize()

        assertTrue(trackedFile.exists())
        assertEquals(trackedFile.absolutePath, queue.byId(tracked.id)?.stagedFilePath)
        assertFalse(untrackedStage.exists())
        assertFalse(untrackedPart.exists())
        assertTrue(manualFile.exists())
        assertEquals(manualFile.absolutePath, queue.byId("manual-job")?.stagedFilePath)
        assertTrue(shareFile.exists())
        assertEquals(shareFile.absolutePath, queue.byId("share-job")?.stagedFilePath)
    }

    @Test
    fun cancellationAfterPublishingButBeforeRoomSaveDeletesUntrackedStage() = runBlocking {
        val media = media(61, byteArrayOf(1, 2, 3, 4))
        val access = FakeBackupMediaAccess(media)
        val published = AtomicReference<File>()
        val queue = queue(
            access,
            afterPublished = { file ->
                published.set(file)
                assertTrue(file.exists())
                throw CancellationException("cancel between publish and Room save")
            },
        )
        assertTrue(queue.enqueueBackupIfAbsent(media))
        val job = requireNotNull(database.uploadDao().backupByMediaKey(media.mediaKey))

        try {
            queue.prepareBackup(job.id, database.mediaCloudStateDao()) { }
            throw AssertionError("expected cancellation")
        } catch (_: CancellationException) {
            // Expected cancellation point after atomic publish and before the Room transaction.
        }

        assertNull(queue.byId(job.id)?.stagedFilePath)
        assertFalse(requireNotNull(published.get()).exists())
        assertFalse(File(queueDirectory, "backup-${job.id}.part").exists())
    }

    @Test
    fun cancellationAfterRoomCommitKeepsTheTrackedStage() = runBlocking {
        val media = media(62, byteArrayOf(5, 6, 7, 8))
        val queue = queue(
            FakeBackupMediaAccess(media),
            afterSaved = { throw CancellationException("cancel after Room commit") },
        )
        assertTrue(queue.enqueueBackupIfAbsent(media))
        val job = requireNotNull(database.uploadDao().backupByMediaKey(media.mediaKey))

        try {
            queue.prepareBackup(job.id, database.mediaCloudStateDao()) { }
            throw AssertionError("expected cancellation")
        } catch (_: CancellationException) {
            // Cleanup must observe the committed Room path and keep its file.
        }

        val saved = requireNotNull(queue.byId(job.id))
        assertNotNull(saved.mediaHash)
        assertNotNull(saved.stagedFilePath)
        assertTrue(File(requireNotNull(saved.stagedFilePath)).exists())
    }

    private fun queue(
        mediaAccess: BackupMediaAccess = object : BackupMediaAccess {
            override suspend fun findByKey(mediaKey: String): DeviceMedia? = null
            override fun open(media: DeviceMedia) = null
        },
        afterPublished: suspend (File) -> Unit = {},
        afterSaved: suspend (File) -> Unit = {},
    ) = UploadQueueStore(
        context = context,
        dao = database.uploadDao(),
        database = database,
        backupMediaAccess = mediaAccess,
        queueDirectory = queueDirectory,
        afterBackupStagePublished = afterPublished,
        afterBackupStageSaved = afterSaved,
    )

    private fun stagedFile(number: Int) = File(queueDirectory, "backup-${backupId(number)}.stage").absolutePath

    private fun backupJob(number: Int, phase: UploadPhase, stagedPath: String?) = UploadJob(
        id = backupId(number),
        source = UploadSource.BACKUP,
        stagedFilePath = stagedPath,
        mediaKey = "image:$number",
        mediaHash = "hash-$number",
        fileName = "photo-$number.jpg",
        mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = null,
        phase = phase,
        idempotencyKey = "idempotency-$number",
        sessionId = "session-$number",
        partSize = 1024,
        preparedFileId = "prepared-$number",
        bytesTotal = 16,
        bytesSent = 0,
        errorMessage = null,
        createdAtMillis = number.toLong(),
        completedAtMillis = null,
    )

    private fun backupId(number: Int) = "backup-" + number.toString(16).padStart(64, '0')

    private fun media(id: Long, bytes: ByteArray): DeviceMedia {
        val source = File(queueDirectory, "media-$id.jpg").apply { writeBytes(bytes) }
        return DeviceMedia(
            id = id,
            uri = Uri.fromFile(source),
            isVideo = false,
            name = source.name,
            dateTakenMillis = 1_700_000_000_000,
            dateModifiedSeconds = 1_700_000_000,
            sizeBytes = bytes.size.toLong(),
            folderKey = "volume:folder",
            folderName = "folder",
            mimeType = "image/jpeg",
        )
    }

    private class FakeBackupMediaAccess(private val mediaByKey: Map<String, DeviceMedia>) : BackupMediaAccess {
        constructor(media: DeviceMedia) : this(mapOf(media.mediaKey to media))

        override suspend fun findByKey(mediaKey: String) = mediaByKey[mediaKey]
        override fun open(media: DeviceMedia) = FileInputStream(File(requireNotNull(media.uri.path)))
    }
}
