package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadForegroundGateTest {
    private val tally = UploadRunTally(done = 1, remaining = 3, failed = 0)

    @Test
    fun dismissalDuringTallyCancelsPublication() = runTest {
        val gate = UploadForegroundGate()
        val tallyStarted = CompletableDeferred<Unit>()
        val releaseTally = CompletableDeferred<Unit>()
        val posted = mutableListOf<UploadRunTally>()

        val refresh = launch {
            gate.publish(
                snapshot = {
                    tallyStarted.complete(Unit)
                    releaseTally.await()
                    tally
                },
                post = { posted += it },
            )
        }
        tallyStarted.await()
        gate.markDismissed()
        releaseTally.complete(Unit)
        refresh.join()

        assertTrue(posted.isEmpty())
    }

    @Test
    fun dismissalBeforeRefreshSkipsSnapshotAndPost() = runTest {
        val gate = UploadForegroundGate()
        var snapshots = 0
        val posted = mutableListOf<UploadRunTally>()
        gate.markDismissed()

        gate.publish(snapshot = { snapshots++; tally }, post = { posted += it })

        assertEquals(0, snapshots)
        assertTrue(posted.isEmpty())
    }

    @Test
    fun dismissalWhileOlderPostIsInFlightStopsLaterUpdates() = runTest {
        val gate = UploadForegroundGate()
        val postStarted = CompletableDeferred<Unit>()
        val releasePost = CompletableDeferred<Unit>()
        val posted = mutableListOf<UploadRunTally>()

        val inFlight = launch {
            gate.publish(
                snapshot = { tally },
                post = {
                    posted += it
                    postStarted.complete(Unit)
                    releasePost.await()
                },
            )
        }
        postStarted.await()
        gate.markDismissed()
        releasePost.complete(Unit)
        inFlight.join()

        // Уже начатый вызов завершился; следующий refresh не должен переписать уведомление.
        gate.publish(snapshot = { tally.copy(done = 2) }, post = { posted += it })

        assertEquals(listOf(tally), posted)
    }

    @Test
    fun updateWaitingBehindInFlightPostIsSuppressedByDismissal() = runTest {
        val gate = UploadForegroundGate()
        val postStarted = CompletableDeferred<Unit>()
        val releasePost = CompletableDeferred<Unit>()
        val posted = mutableListOf<UploadRunTally>()

        val inFlight = launch {
            gate.publish(
                snapshot = { tally },
                post = {
                    posted += it
                    postStarted.complete(Unit)
                    releasePost.await()
                },
            )
        }
        postStarted.await()
        val waiting = launch { gate.publish(snapshot = { tally.copy(done = 2) }, post = { posted += it }) }
        gate.markDismissed()
        releasePost.complete(Unit)
        inFlight.join()
        waiting.join()

        assertEquals(listOf(tally), posted)
    }

    @Test
    fun resetAllowsPostingAgainForNextRun() = runTest {
        val gate = UploadForegroundGate()
        val posted = mutableListOf<UploadRunTally>()
        gate.markDismissed()

        gate.reset()
        gate.publish(snapshot = { tally }, post = { posted += it })

        assertEquals(listOf(tally), posted)
    }

    @Test
    fun emptySnapshotPostsNothing() = runTest {
        val gate = UploadForegroundGate()
        val posted = mutableListOf<UploadRunTally>()

        gate.publish(snapshot = { null }, post = { posted += it })

        assertTrue(posted.isEmpty())
    }
}
