package com.barkfluff.BarkCloud.data

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class SessionManagerCleanupTest {
    @Test
    fun logoutWaitsForDiscoveryAndUploadCancellationBeforeClearingQueue() = runTest {
        val steps = mutableListOf<String>()

        cancelUploadWorkBeforeQueueClear(
            cancelDiscovery = { steps += "discovery-cancelled" },
            cancelUploads = { steps += "uploads-cancelled" },
            clearLocalQueueAndState = { steps += "queue-cleared" },
        )

        assertEquals(listOf("discovery-cancelled", "uploads-cancelled", "queue-cleared"), steps)
    }
}
