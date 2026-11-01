package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class DedupCompletionTest {

    private class Harness(vararg reads: DedupRead, private val cas: List<DedupCas>) {
        private val readQueue = ArrayDeque(reads.toList())
        private val casQueue = ArrayDeque(cas)
        val addedAlbums = mutableListOf<String>()
        var casCalls = 0
        var readCalls = 0
        private var lastRead: DedupRead = reads.lastOrNull() ?: DedupRead.Inactive

        suspend fun read(): DedupRead {
            readCalls++
            readQueue.removeFirstOrNull()?.let { lastRead = it }
            return lastRead
        }

        suspend fun add(album: String, fileId: String) {
            addedAlbums += "$album:$fileId"
        }

        suspend fun complete(expected: String?): DedupCas {
            casCalls++
            return casQueue.removeFirstOrNull() ?: DedupCas.Retry
        }

        suspend fun run(fileId: String? = "copy-1") = completeDeduplicatedWithAlbum(
            fileId = fileId,
            read = ::read,
            addToAlbum = ::add,
            completeIfAlbum = ::complete,
        )
    }

    @Test
    fun inactiveJobAtReadExitsWithoutAlbumOrCompletion() = runTest {
        val h = Harness(DedupRead.Inactive, cas = emptyList())

        assertEquals(DedupOutcome.Inactive, h.run())
        assertEquals(emptyList<String>(), h.addedAlbums)
        assertEquals(0, h.casCalls)
    }

    @Test
    fun liveJobWithoutAlbumCompletesOnFirstCas() = runTest {
        val h = Harness(DedupRead.Live(albumId = null), cas = listOf(DedupCas.Done))

        assertEquals(DedupOutcome.Completed(albumAdded = false), h.run())
        assertEquals(1, h.casCalls)
    }

    @Test
    fun cancelOrPauseBetweenAlbumReadAndCasDoesNotPublishCompletion() = runTest {
        // Альбом прочитан и добавлен, затем задача отменена/приостановлена до CAS.
        val h = Harness(DedupRead.Live("album-a"), cas = listOf(DedupCas.Inactive))

        assertEquals(DedupOutcome.Inactive, h.run())
        assertEquals(listOf("album-a:copy-1"), h.addedAlbums)
        assertEquals(1, h.casCalls)
    }

    @Test
    fun albumChangedDuringCompletionIsAddedAndCompletionRetried() = runTest {
        val h = Harness(
            DedupRead.Live("album-a"),
            DedupRead.Live("album-b"),
            cas = listOf(DedupCas.Retry, DedupCas.Done),
        )

        assertEquals(DedupOutcome.Completed(albumAdded = true), h.run())
        assertEquals(listOf("album-a:copy-1", "album-b:copy-1"), h.addedAlbums)
        assertEquals(2, h.casCalls)
    }

    @Test
    fun cancelAfterAlbumChangeStopsWithoutFurtherAlbumRequests() = runTest {
        val h = Harness(
            DedupRead.Live("album-a"),
            DedupRead.Inactive,
            cas = listOf(DedupCas.Retry),
        )

        assertEquals(DedupOutcome.Inactive, h.run())
        assertEquals(listOf("album-a:copy-1"), h.addedAlbums)
    }

    @Test
    fun endlessAlbumChangesTerminateWithBoundedRequests() = runTest {
        // Альбом меняется при каждом CAS: цикл обязан остановиться на лимите попыток.
        val h = Harness(DedupRead.Live("album-x"), cas = List(100) { DedupCas.Retry })

        assertEquals(DedupOutcome.Exhausted, h.run())
        assertEquals(MAX_DEDUP_ATTEMPTS, h.casCalls)
        assertEquals(MAX_DEDUP_ATTEMPTS, h.addedAlbums.size)
    }

    @Test
    fun albumWithoutCopyIdFailsWithoutAlbumRequest() = runTest {
        val h = Harness(DedupRead.Live("album-a"), cas = emptyList())

        assertEquals(DedupOutcome.MissingFileId, h.run(fileId = null))
        assertEquals(emptyList<String>(), h.addedAlbums)
        assertEquals(0, h.casCalls)
    }
}
