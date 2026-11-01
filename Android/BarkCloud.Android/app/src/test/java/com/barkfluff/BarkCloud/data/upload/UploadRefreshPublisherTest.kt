package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadRefreshPublisherTest {
    private val tally = UploadRunTally(done = 1, remaining = 3, failed = 0)

    @Test
    fun dismissalDeliveredWhileSnapshotIsHeldCancelsNotify() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var dismissed = false
        val notified = mutableListOf<UploadRunTally>()
        val snapshotStarted = CompletableDeferred<Unit>()
        val releaseSnapshot = CompletableDeferred<Unit>()
        val publisher = UploadRefreshPublisher(main, isDismissed = { dismissed }, notify = { notified += it })

        val refresh = launch {
            publisher.publish {
                snapshotStarted.complete(Unit)
                releaseSnapshot.await()
                tally
            }
        }
        snapshotStarted.await()
        // Receiver приходит на главном потоке раньше, чем refresh дойдёт до финальной проверки.
        launch(main) { dismissed = true }
        releaseSnapshot.complete(Unit)
        refresh.join()

        assertTrue(notified.isEmpty())
    }

    @Test
    fun dismissalAfterNotifyStopsLaterRefreshes() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var dismissed = false
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, isDismissed = { dismissed }, notify = { notified += it })

        publisher.publish { tally }
        assertEquals(listOf(tally), notified)

        launch(main) { dismissed = true }.join()
        publisher.publish { tally.copy(done = 2) }

        assertEquals(listOf(tally), notified)
    }

    @Test
    fun dismissedBeforeRefreshSkipsSnapshot() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var snapshots = 0
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, isDismissed = { true }, notify = { notified += it })

        publisher.publish {
            snapshots++
            tally
        }

        assertEquals(0, snapshots)
        assertTrue(notified.isEmpty())
    }

    @Test
    fun emptySnapshotNotifiesNothing() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, isDismissed = { false }, notify = { notified += it })

        publisher.publish { null }

        assertTrue(notified.isEmpty())
    }

    @Test
    fun refreshesNotifyInOrderWhileNotDismissed() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, isDismissed = { false }, notify = { notified += it })

        publisher.publish { tally }
        publisher.publish { tally.copy(done = 2, remaining = 2) }

        assertEquals(listOf(tally, tally.copy(done = 2, remaining = 2)), notified)
    }
}
