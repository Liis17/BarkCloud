package com.barkfluff.BarkCloud.data.gallery

import android.net.Uri
import io.mockk.mockk
import com.barkfluff.BarkCloud.data.cloud.CHECK_HASHES_BATCH
import com.barkfluff.BarkCloud.data.cloud.HashPresence
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CloudPresenceCheckerTest {

    private val hashA = "a".repeat(64)
    private val hashB = "b".repeat(64)

    @Test
    fun cachedHashIsReusedWhenMetadataMatches() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(1)
        dao.rows[media.mediaKey] = cloudStateOf(media, hashA, MediaCloudStatus.NOT_IN_CLOUD)
        var hashed = 0
        val checker = checker(this, dao, hashFile = { hashed++; hashB }, presence = { chunk ->
            chunk.associateWith { HashPresence(true, listOf("file-1")) }
        })

        checker.request(media)
        checker.flush()

        assertEquals(0, hashed)
        assertEquals(MediaCloudStatus.IN_CLOUD, dao.rows.getValue(media.mediaKey).status)
        assertEquals("file-1", dao.rows.getValue(media.mediaKey).cloudFileId)
    }

    @Test
    fun changedMetadataInvalidatesCachedHashOfSettledRow() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(2, size = 200)
        dao.rows[media.mediaKey] = cloudStateOf(media.copy(sizeBytes = 100), hashA, MediaCloudStatus.IN_CLOUD)
        val checker = checker(this, dao, hashFile = { hashB }, presence = { chunk ->
            chunk.associateWith { HashPresence(false, emptyList()) }
        })

        checker.request(media)
        checker.flush()

        val row = dao.rows.getValue(media.mediaKey)
        assertEquals(hashB, row.hash)
        assertEquals(200L, row.sizeBytes)
        assertEquals(MediaCloudStatus.NOT_IN_CLOUD, row.status)
    }

    @Test
    fun changedMetadataDoesNotTouchQueuedUpload() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(8, size = 200)
        dao.rows[media.mediaKey] = cloudStateOf(media.copy(sizeBytes = 100), hashA, MediaCloudStatus.QUEUED)
        val checker = checker(this, dao, hashFile = { hashB }, presence = { chunk ->
            chunk.associateWith { HashPresence(false, emptyList()) }
        })

        checker.request(media)
        checker.flush()

        // Идущая загрузка сама проверит источник; проверка присутствия её не перетирает.
        val row = dao.rows.getValue(media.mediaKey)
        assertEquals(MediaCloudStatus.QUEUED, row.status)
        assertEquals(hashA, row.hash)
        assertEquals(100L, row.sizeBytes)
    }

    @Test
    fun lateAbsentAnswerDoesNotDowngradeInCloud() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(3)
        dao.rows[media.mediaKey] = cloudStateOf(media, hashA, MediaCloudStatus.IN_CLOUD)
        val checker = checker(this, dao, hashFile = { hashA }, presence = { chunk ->
            chunk.associateWith { HashPresence(false, emptyList()) }
        })

        checker.request(media)
        checker.flush()

        assertEquals(MediaCloudStatus.IN_CLOUD, dao.rows.getValue(media.mediaKey).status)
    }

    @Test
    fun missingServerAnswerLeavesStateUnchanged() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(4)
        dao.rows[media.mediaKey] = cloudStateOf(media, hashA, MediaCloudStatus.NOT_IN_CLOUD)
        val checker = checker(this, dao, hashFile = { hashA }, presence = { emptyMap() })

        checker.request(media)
        checker.flush()

        assertEquals(MediaCloudStatus.NOT_IN_CLOUD, dao.rows.getValue(media.mediaKey).status)
        assertNull(dao.rows.getValue(media.mediaKey).cloudFileId)
    }

    @Test
    fun exists_withoutIds_marksPresentWithoutFileId() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(5)
        dao.rows[media.mediaKey] = cloudStateOf(media, hashA, MediaCloudStatus.NOT_IN_CLOUD)
        val checker = checker(this, dao, hashFile = { hashA }, presence = { chunk ->
            chunk.associateWith { HashPresence(true, emptyList()) }
        })

        checker.request(media)
        checker.flush()

        val row = dao.rows.getValue(media.mediaKey)
        assertEquals(MediaCloudStatus.IN_CLOUD, row.status)
        assertNull(row.cloudFileId)
    }

    @Test
    fun largeLibraryIsCheckedInServerSizedBatches() = runTest {
        val dao = FakeMediaCloudStateDao()
        val medias = (0 until CHECK_HASHES_BATCH + 10).map { media(100L + it) }
        val batches = mutableListOf<Int>()
        val checker = checker(this, dao, hashFile = { item -> "%064x".format(item.id) }, presence = { chunk ->
            batches += chunk.size
            chunk.associateWith { HashPresence(false, emptyList()) }
        })

        medias.forEach { checker.request(it) }
        checker.flush()

        assertTrue(batches.all { it <= CHECK_HASHES_BATCH })
        assertEquals(medias.size, batches.sum())
    }

    @Test
    fun cancelledCheckLeavesNoPersistentCheckingState() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(6)
        val checker = checker(this, dao, hashFile = { hashA }, presence = { throw CancellationException("stop") })

        checker.request(media)
        // Отмена прерывает проверку; CancellationException пробрасывается, а состояние не сохраняется.
        var cancelled: CancellationException? = null
        try {
            checker.flush()
        } catch (error: CancellationException) {
            cancelled = error
        }

        assertTrue(cancelled != null)
        assertEquals(MediaCloudStatus.NOT_IN_CLOUD, dao.rows.getValue(media.mediaKey).status)
        assertTrue(dao.rows.values.none { it.status == MediaCloudStatus.CHECKING })
    }

    @Test
    fun recentlyCheckedItemIsThrottledUnlessForced() = runTest {
        val dao = FakeMediaCloudStateDao()
        val media = media(7)
        var calls = 0
        val checker = checker(this, dao, hashFile = { hashA }, presence = { chunk ->
            calls++
            chunk.associateWith { HashPresence(false, emptyList()) }
        })

        checker.request(media)
        checker.flush()
        checker.request(media)
        checker.flush()
        assertEquals(1, calls)

        checker.request(media, force = true)
        checker.flush()
        assertEquals(2, calls)
    }

    private fun checker(
        scope: TestScope,
        dao: MediaCloudStateDao,
        hashFile: suspend (DeviceMedia) -> String?,
        presence: suspend (List<String>) -> Map<String, HashPresence>,
        now: () -> Long = { 0L },
    ) = CloudPresenceChecker(
        scope = scope,
        mediaDao = dao,
        hashFile = hashFile,
        presence = presence,
        now = now,
        debounceMillis = 0L,
        recheckAfterMillis = 30_000L,
    )

    private fun media(id: Long, size: Long = 100L) = DeviceMedia(
        id = id,
        uri = mockk(relaxed = true),
        isVideo = false,
        name = "f$id.jpg",
        dateTakenMillis = 0L,
        dateModifiedSeconds = 10L,
        sizeBytes = size,
    )
}
