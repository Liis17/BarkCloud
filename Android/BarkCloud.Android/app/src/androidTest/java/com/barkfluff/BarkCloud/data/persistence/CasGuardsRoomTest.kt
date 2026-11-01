package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * SQL-условия CAS-защит на настоящей Room (in-memory): дедупликация с альбомом и поколение строки
 * присутствия. Запускается на устройстве или эмуляторе (`connectedDebugAndroidTest`).
 */
@RunWith(AndroidJUnit4::class)
class CasGuardsRoomTest {

    private lateinit var database: BarkCloudDatabase

    @Before
    fun setUp() {
        database = Room.inMemoryDatabaseBuilder(
            InstrumentationRegistry.getInstrumentation().targetContext,
            BarkCloudDatabase::class.java,
        ).allowMainThreadQueries().build()
    }

    @After
    fun tearDown() {
        database.close()
    }

    private fun job(id: String, phase: UploadPhase, albumId: String?) = UploadJob(
        id = id,
        source = UploadSource.BACKUP,
        stagedFilePath = null,
        mediaKey = "image:$id",
        mediaHash = "h",
        fileName = "$id.jpg",
        mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = albumId,
        phase = phase,
        idempotencyKey = null,
        sessionId = null,
        partSize = 0L,
        preparedFileId = null,
        bytesTotal = 1L,
        bytesSent = 0L,
        errorMessage = null,
        createdAtMillis = 1L,
        completedAtMillis = null,
    )

    @Test
    fun dedupCasCompletesOnlyWhileAlbumMatchesAndJobIsLive() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("live", UploadPhase.QUEUED, albumId = "a"))
        dao.insert(job("changed", UploadPhase.QUEUED, albumId = "b"))
        dao.insert(job("cancelled", UploadPhase.CANCELLED, albumId = "a"))

        assertEquals(1, dao.completeDeduplicatedIfAlbum("live", "a", 10L))
        assertEquals(0, dao.completeDeduplicatedIfAlbum("changed", "a", 10L)) // альбом изменился
        assertEquals(0, dao.completeDeduplicatedIfAlbum("cancelled", "a", 10L)) // задача неактивна
        assertEquals(UploadPhase.CANCELLED, dao.byId("cancelled")!!.phase)
    }

    @Test
    fun albumRequestIsRejectedAfterAttachBegins() = runBlocking {
        val dao = database.uploadDao()
        dao.insert(job("queued", UploadPhase.QUEUED, albumId = null))
        dao.insert(job("attaching", UploadPhase.ATTACHING, albumId = null))

        assertEquals(1, dao.setAlbumIfPreAttach("queued", "album"))
        assertEquals(0, dao.setAlbumIfPreAttach("attaching", "album"))
        assertEquals(null, dao.byId("attaching")!!.albumId)
    }

    @Test
    fun staleAbsenceDoesNotApplyWhenRowGenerationChanged() = runBlocking {
        val dao = database.mediaCloudStateDao()
        val base = MediaCloudState("image:1", 1L, false, 10L, 100L, "h", MediaCloudStatus.IN_CLOUD, "old")
        dao.upsert(base)

        // Снимок «IN_CLOUD/old» устарел: появилась новая загрузка.
        dao.upsert(base.copy(cloudFileId = "new"))
        assertEquals(0, dao.markAbsent("image:1", 10L, 100L, "h", MediaCloudStatus.IN_CLOUD, "old"))
        assertEquals("new", dao.byKey("image:1")!!.cloudFileId)

        // Актуальный снимок и авторитетное отсутствие снимают значок.
        assertEquals(1, dao.markAbsent("image:1", 10L, 100L, "h", MediaCloudStatus.IN_CLOUD, "new"))
        assertEquals(MediaCloudStatus.NOT_IN_CLOUD, dao.byKey("image:1")!!.status)
    }
}
