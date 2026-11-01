package com.barkfluff.BarkCloud.ui.selection

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class CloudBatchTest {

    @Test
    fun partialFailureKeepsFailedIdsOutOfSucceededSet() = runTest {
        val outcome = runCloudBatch(listOf("a", "b", "c")) { id ->
            if (id == "b") throw IllegalStateException("network")
        }

        assertEquals(setOf("a", "c"), outcome.succeeded)
        assertEquals(1, outcome.failed)
        assertEquals(3, outcome.total)
    }

    @Test
    fun cancellationStopsBatchAndIsNotSwallowed() = runTest {
        val attempted = mutableListOf<String>()

        var thrown: CancellationException? = null
        try {
            runCloudBatch(listOf("a", "b", "c")) { id ->
                attempted += id
                if (id == "b") throw CancellationException("cancelled")
            }
        } catch (error: CancellationException) {
            thrown = error
        }

        assertEquals(listOf("a", "b"), attempted)
        assertEquals(true, thrown != null)
    }

    @Test
    fun emptyInputProducesEmptyOutcome() = runTest {
        val outcome = runCloudBatch(emptyList()) { error("not called") }

        assertEquals(emptySet<String>(), outcome.succeeded)
        assertEquals(0, outcome.failed)
    }
}
