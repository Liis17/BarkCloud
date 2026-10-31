package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class UploadStartupRestorationTest {
    @Test
    fun processRestartWithSamePolicyReenqueuesPersistedActiveQueueOnce() = runTest {
        val persistedActiveJobs = 2
        var enqueueCount = 0

        restoreActiveUploadQueueAtStartup(
            policy = AutoUploadNetworkPolicy.WIFI_ONLY,
            initializeQueue = {},
            activeCount = { persistedActiveJobs },
            activeNonBackupCount = { 0 },
            enqueue = { enqueueCount++ },
        )

        assertEquals(1, enqueueCount)
    }

    @Test
    fun offPolicyDoesNotScheduleOnlyBackupQueueButKeepsManualQueueEligible() = runTest {
        var backupOnlyEnqueues = 0
        restoreActiveUploadQueueAtStartup(
            policy = AutoUploadNetworkPolicy.OFF,
            initializeQueue = {},
            activeCount = { 2 },
            activeNonBackupCount = { 0 },
            enqueue = { backupOnlyEnqueues++ },
        )

        var manualEnqueues = 0
        restoreActiveUploadQueueAtStartup(
            policy = AutoUploadNetworkPolicy.OFF,
            initializeQueue = {},
            activeCount = { 1 },
            activeNonBackupCount = { 1 },
            enqueue = { manualEnqueues++ },
        )

        assertEquals(0, backupOnlyEnqueues)
        assertEquals(1, manualEnqueues)
    }

    @Test
    fun restartDoesNotScheduleWhenOnlyPausedOrFailedRowsRemain() = runTest {
        var enqueued = false
        restoreActiveUploadQueueAtStartup(
            policy = AutoUploadNetworkPolicy.ANY_NETWORK,
            initializeQueue = {},
            activeCount = { 0 },
            activeNonBackupCount = { 0 },
            enqueue = { enqueued = true },
        )

        assertFalse(enqueued)
    }
}
