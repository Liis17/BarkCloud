package com.barkfluff.BarkCloud.data.cloud

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class CloudCopyTest {

    @Test
    fun missingServerAnswerIsUnknownNotAbsent() {
        assertEquals(CloudCopy.Unknown, CloudCopy.of(null))
    }

    @Test
    fun notExistingIsAbsent() {
        assertEquals(CloudCopy.Absent, CloudCopy.of(HashPresence(exists = false, fileIds = emptyList())))
    }

    @Test
    fun existsWithoutActionableIdsIsUnknownForActions() {
        // Старый сервер: exists=true без file_ids — действие по хешу недоступно.
        assertEquals(CloudCopy.Unknown, CloudCopy.of(HashPresence(exists = true, fileIds = emptyList())))
    }

    @Test
    fun presentPicksLowestIdDeterministically() {
        val copy = CloudCopy.of(HashPresence(exists = true, fileIds = listOf("b", "a", "c")))

        assertEquals(CloudCopy.Present("a", listOf("a", "b", "c")), copy)
    }

    @Test
    fun batchedCheckSplitsAtServerLimitAndNormalizesHashes() = runTest {
        val hashes = (0 until 1_201).map { index -> "%064x".format(index) } + "%064X".format(0)
        val requests = mutableListOf<List<String>>()

        val result = checkPresenceBatched(hashes) { chunk ->
            requests += chunk
            chunk.associateWith { HashPresence(exists = true, fileIds = listOf("id-$it")) }
        }

        assertEquals(listOf(CHECK_HASHES_BATCH, CHECK_HASHES_BATCH, 1_201 - 2 * CHECK_HASHES_BATCH), requests.map { it.size })
        assertTrue(requests.all { it.size <= CHECK_HASHES_BATCH })
        assertEquals(1_201, result.size)
    }

    @Test
    fun hashWithoutServerRowStaysUnknown() = runTest {
        val present = "a".repeat(64)
        val missing = "b".repeat(64)

        val result = checkPresenceBatched(listOf(present, missing)) { chunk ->
            chunk.filter { it == present }.associateWith { HashPresence(exists = true, fileIds = listOf("x")) }
        }

        assertEquals(setOf(present), result.keys)
        assertEquals(CloudCopy.Unknown, CloudCopy.of(result[missing]))
    }
}
