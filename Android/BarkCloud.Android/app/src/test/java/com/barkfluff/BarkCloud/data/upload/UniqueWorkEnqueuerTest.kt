package com.barkfluff.BarkCloud.data.upload

import androidx.work.ExistingWorkPolicy
import androidx.work.WorkInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UniqueWorkEnqueuerTest {
    @Test
    fun enqueuedWorkCoalescesAndRunningWorkGetsOneSuccessor() {
        assertNull(UniqueWorkEnqueuer.policyFor(listOf(WorkInfo.State.ENQUEUED)))
        assertEquals(
            ExistingWorkPolicy.APPEND_OR_REPLACE,
            UniqueWorkEnqueuer.policyFor(listOf(WorkInfo.State.RUNNING)),
        )
    }

    @Test
    fun terminalOrMissingWorkAllowsFreshEnqueue() {
        assertEquals(ExistingWorkPolicy.KEEP, UniqueWorkEnqueuer.policyFor(emptyList()))
        assertEquals(
            ExistingWorkPolicy.KEEP,
            UniqueWorkEnqueuer.policyFor(listOf(WorkInfo.State.SUCCEEDED, WorkInfo.State.FAILED)),
        )
    }

    @Test
    fun enqueuedSuccessorCoalescesEvenWhenAnotherWorkIsRunning() {
        assertNull(
            UniqueWorkEnqueuer.policyFor(listOf(WorkInfo.State.RUNNING, WorkInfo.State.ENQUEUED)),
        )
    }
}
