package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
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

    @Test
    @OptIn(ExperimentalCoroutinesApi::class)
    fun logoutDuringQueueInitializationPreventsEnqueueAndPolicyApply() = runTest {
        val sessionActive = MutableStateFlow(true)
        val initializationStarted = CompletableDeferred<Unit>()
        val continueInitialization = CompletableDeferred<Unit>()
        val effects = mutableListOf<String>()

        backgroundScope.launch {
            observeUploadStartupForSession(
                sessionActive = sessionActive,
                readPolicy = { AutoUploadNetworkPolicy.WIFI_ONLY },
                initializeQueue = {
                    initializationStarted.complete(Unit)
                    withContext(NonCancellable) { continueInitialization.await() }
                },
                activeCount = { 1 },
                activeNonBackupCount = { 0 },
                enqueue = { effects += "enqueue" },
                applyPolicy = { effects += "apply" },
            )
        }
        runCurrent()
        initializationStarted.await()

        sessionActive.value = false
        continueInitialization.complete(Unit)
        runCurrent()

        assertTrue(effects.isEmpty())
    }
}
