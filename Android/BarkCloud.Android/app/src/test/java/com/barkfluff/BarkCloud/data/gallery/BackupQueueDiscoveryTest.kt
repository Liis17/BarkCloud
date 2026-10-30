package com.barkfluff.BarkCloud.data.gallery

import android.net.Uri
import io.mockk.mockk
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BackupQueueDiscoveryTest {
    @Test
    fun sevenSelectedFoldersQueueAllPhotoAndVideoMetadataBeyondTwoHundred() = runTest {
        val media = (1L..301L).map { id ->
            val folder = "volume:${(id - 1) % 7}"
            DeviceMedia(
                id = id,
                uri = mockk<Uri>(),
                isVideo = id % 3L == 0L,
                name = "media-$id.${if (id % 3L == 0L) "mp4" else "jpg"}",
                dateTakenMillis = id * 1000L,
                dateModifiedSeconds = id * 10L,
                sizeBytes = id * 100L,
                folderKey = folder,
                folderName = "Folder ${(id - 1) % 7}",
                mimeType = if (id % 3L == 0L) "video/mp4" else "image/jpeg",
            )
        }
        val queue = FakeBackupQueue()
        val selected = (0..6).map { "volume:$it" }.toSet()
        val discovery = BackupQueueDiscovery(queue, isEnabled = { true }, includesFolder = selected::contains)

        val added = discovery.discover { consume -> media.chunked(47).forEach { consume(it) } }

        assertEquals(301, added)
        assertEquals(301, queue.rows.size)
        assertTrue(queue.rows.values.any { it.media.isVideo })
        assertTrue(queue.rows.values.any { !it.media.isVideo })
        queue.rows.values.forEach { row ->
            assertTrue(row.media.folderKey in selected)
            assertEquals(row.media.name, row.fileName)
            assertEquals(row.media.sizeBytes, row.sizeBytes)
            assertEquals(row.media.mimeType, row.mimeType)
            assertNull(row.stagedFilePath)
            assertNull(row.mediaHash)
        }
    }

    @Test
    fun parallelAndRepeatedScansDeduplicateAndUnchangedFailedMediaStaysFailed() = runTest {
        val media = (1L..250L).map { id ->
            DeviceMedia(id, mockk<Uri>(), id % 2L == 0L, "$id.jpg", id, id, id * 10, "v:$id", "Folder", "image/jpeg")
        }
        val queue = FakeBackupQueue()
        val discovery = BackupQueueDiscovery(queue, isEnabled = { true }, includesFolder = { true })
        val reader: suspend (suspend (List<DeviceMedia>) -> Unit) -> Unit = { consume ->
            media.chunked(31).forEach { consume(it) }
        }

        val counts = listOf(async { discovery.discover(reader) }, async { discovery.discover(reader) }).awaitAll()
        assertEquals(250, counts.sum())
        assertEquals(250, queue.rows.size)

        val failedRow = queue.rows.getValue(media.first().mediaKey)
        failedRow.phase = BackupQueuePhase.FAILED
        assertEquals(0, discovery.discover(reader))
        assertEquals(BackupQueuePhase.FAILED, queue.rows.getValue(media.first().mediaKey).phase)

        val changed = media.first().copy(dateModifiedSeconds = media.first().dateModifiedSeconds + 1)
        assertEquals(1, discovery.discover { consume -> consume(listOf(changed)) })
        assertEquals(250, queue.rows.size)
        assertEquals(BackupQueuePhase.QUEUED, queue.rows.getValue(changed.mediaKey).phase)
    }

    @Test
    fun disabledAndDeselectedFoldersAreNotAdded() = runTest {
        val media = DeviceMedia(1, mockk(), false, "one.jpg", 1, 1, 10, "volume:1", "Folder", "image/jpeg")
        val queue = FakeBackupQueue()

        val disabled = BackupQueueDiscovery(queue, isEnabled = { false }, includesFolder = { true })
        assertEquals(0, disabled.discover { it(listOf(media)) })
        val deselected = BackupQueueDiscovery(queue, isEnabled = { true }, includesFolder = { false })
        assertEquals(0, deselected.discover { it(listOf(media)) })
        assertTrue(queue.rows.isEmpty())
    }

    @Test
    fun discoveryWithNoNewMediaStillWakesExistingQueue() = runTest {
        var scheduled = 0
        val discovered = discoverAndWakeUploadQueue(
            discover = { 0 },
            activeCount = { 3 },
            enqueueUpload = { scheduled++ },
        )

        assertEquals(0, discovered)
        assertEquals(1, scheduled)
    }

    private class FakeBackupQueue : BackupUploadQueue {
        val rows = linkedMapOf<String, FakeBackupRow>()

        override suspend fun enqueueBackupIfAbsent(media: DeviceMedia): Boolean {
            val existing = rows[media.mediaKey]
            if (existing == null) {
                rows[media.mediaKey] = FakeBackupRow(media)
                return true
            }
            val unchanged = existing.media.dateModifiedSeconds == media.dateModifiedSeconds &&
                existing.media.sizeBytes == media.sizeBytes && existing.media.name == media.name
            if (unchanged && existing.phase in setOf(BackupQueuePhase.QUEUED, BackupQueuePhase.FAILED, BackupQueuePhase.UPLOADED)) return false
            existing.media = media
            existing.fileName = media.name
            existing.sizeBytes = media.sizeBytes
            existing.mimeType = media.mimeType
            existing.mediaHash = null
            existing.stagedFilePath = null
            existing.phase = BackupQueuePhase.QUEUED
            return true
        }
    }

    private data class FakeBackupRow(
        var media: DeviceMedia,
        var fileName: String = media.name,
        var sizeBytes: Long = media.sizeBytes,
        var mimeType: String? = media.mimeType,
        var mediaHash: String? = null,
        var stagedFilePath: String? = null,
        var phase: BackupQueuePhase = BackupQueuePhase.QUEUED,
    )

    private enum class BackupQueuePhase { QUEUED, FAILED, UPLOADED }
}
