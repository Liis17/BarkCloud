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
        val publisher = UploadRefreshPublisher(
            mainContext = main,
            isDismissed = { dismissed },
            isRegistered = { true },
            notify = { notified += it },
        )

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
        val publisher = UploadRefreshPublisher(main, { dismissed }, { true }, { notified += it })

        publisher.publish { tally }
        assertEquals(listOf(tally), notified)

        launch(main) { dismissed = true }.join()
        publisher.publish { tally.copy(done = 2) }

        assertEquals(listOf(tally), notified)
    }

    @Test
    fun refreshBeforeRegistrationPostsNothing() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var snapshots = 0
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, { false }, { false }, { notified += it })

        publisher.publish {
            snapshots++
            tally
        }

        assertEquals(0, snapshots)
        assertTrue(notified.isEmpty())
    }

    @Test
    fun registrationIsRecheckedUntilSeenAndThenLatched() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var registered = false
        var registrationChecks = 0
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(
            mainContext = main,
            isDismissed = { false },
            isRegistered = {
                registrationChecks++
                registered
            },
            notify = { notified += it },
        )

        publisher.publish { tally }
        assertTrue(notified.isEmpty())

        registered = true
        publisher.publish { tally.copy(done = 2) }
        assertEquals(listOf(tally.copy(done = 2)), notified)

        // Подтверждённая регистрация на проход не перепроверяется, даже если флаг пропал.
        registered = false
        val checksAfterLatch = registrationChecks
        publisher.publish { tally.copy(done = 3) }
        assertEquals(checksAfterLatch, registrationChecks)
        assertEquals(tally.copy(done = 3), notified.last())
    }

    @Test
    fun dismissedBeforeRefreshSkipsSnapshot() = runTest {
        val main = StandardTestDispatcher(testScheduler)
        var snapshots = 0
        val notified = mutableListOf<UploadRunTally>()
        val publisher = UploadRefreshPublisher(main, { true }, { true }, { notified += it })

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
        val publisher = UploadRefreshPublisher(main, { false }, { true }, { notified += it })

        publisher.publish { null }

        assertTrue(notified.isEmpty())
    }
}
