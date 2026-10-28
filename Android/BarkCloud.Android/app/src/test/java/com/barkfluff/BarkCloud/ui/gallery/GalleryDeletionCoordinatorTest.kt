package com.barkfluff.BarkCloud.ui.gallery

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class GalleryDeletionCoordinatorTest {
    @Test
    fun approvedDeviceCleanupNeverDeletesCloudCopies() = runTest {
        val cloudCalls = mutableListOf<String>()
        val deviceCalls = mutableListOf<String>()
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val coordinator = GalleryDeletionCoordinator(
            scope = scope,
            deleteCloudFile = { cloudCalls += it },
            onDeviceFilesDeleted = { deviceCalls += it },
            onCloudFileDeleted = {},
        )
        val pending = PendingPhotoDelete(listOf("image:1"), emptyList(), 1, accountReclaim = true)

        assertTrue(coordinator.beginSystemConfirmation(pending))
        assertTrue(deviceCalls.isEmpty())
        coordinator.completeSystemConfirmation(pending, approved = true)
        advanceUntilIdle()

        assertEquals(listOf("image:1"), deviceCalls)
        assertTrue(cloudCalls.isEmpty())
        assertTrue(coordinator.state.value.results.single().accountReclaim)
        scope.cancel()
    }

    @Test
    fun cancellingSystemConfirmationPreventsAllCloudDeletes() = runTest {
        val cloudCalls = mutableListOf<String>()
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val coordinator = coordinator(scope, deleteCloud = { cloudCalls += it })
        val pending = mixedDelete()

        assertTrue(coordinator.beginSystemConfirmation(pending))
        coordinator.completeSystemConfirmation(pending, approved = false)
        runCurrent()

        assertTrue(cloudCalls.isEmpty())
        assertFalse(coordinator.state.value.isBusy)
        scope.cancel()
    }

    @Test
    fun systemRequestFailureCanBeAbortedWithoutDeletingCloud() = runTest {
        val cloudCalls = mutableListOf<String>()
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val coordinator = coordinator(scope, deleteCloud = { cloudCalls += it })
        val pending = mixedDelete()

        assertTrue(coordinator.beginSystemConfirmation(pending))
        coordinator.abortSystemConfirmation()
        runCurrent()

        assertTrue(cloudCalls.isEmpty())
        assertFalse(coordinator.state.value.isBusy)
        scope.cancel()
    }

    @Test
    fun mixedDeletionAppliesDeviceAndCloudSuccessesWhileRetainingCloudFailure() = runTest {
        val cloudCalls = mutableListOf<String>()
        val deviceDeletes = mutableListOf<List<String>>()
        val successfulCloudDeletes = mutableListOf<String>()
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val coordinator = GalleryDeletionCoordinator(
            scope = scope,
            deleteCloudFile = {
                cloudCalls += it
                if (it == "cloud-fail") error("server rejected delete")
            },
            onDeviceFilesDeleted = { deviceDeletes += it },
            onCloudFileDeleted = successfulCloudDeletes::add,
        )
        val pending = mixedDelete()

        assertTrue(coordinator.beginSystemConfirmation(pending))
        coordinator.completeSystemConfirmation(pending, approved = true)
        advanceUntilIdle()

        assertEquals(listOf(listOf("device-1")), deviceDeletes)
        assertEquals(listOf("cloud-ok", "cloud-fail"), cloudCalls)
        assertEquals(listOf("cloud-ok"), successfulCloudDeletes)
        assertEquals(setOf("cloud-ok"), coordinator.state.value.deletedCloudIds)
        assertEquals(2, coordinator.state.value.results.single().deletedCount)
        assertEquals(3, coordinator.state.value.results.single().totalCount)
        assertFalse(coordinator.state.value.isBusy)
        scope.cancel()
    }

    @Test
    fun deleteContinuesWhenScreenScopeIsCancelled() = runTest {
        val screenScope = CoroutineScope(Job() + StandardTestDispatcher(testScheduler))
        val viewModelScope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val started = CompletableDeferred<Unit>()
        val finishDelete = CompletableDeferred<Unit>()
        val cloudCalls = mutableListOf<String>()
        val coordinator = coordinator(viewModelScope) {
            cloudCalls += it
            started.complete(Unit)
            finishDelete.await()
        }

        screenScope.launch {
            assertTrue(coordinator.deleteCloudOnly(PendingPhotoDelete(emptyList(), listOf("cloud-1"), 1)))
        }
        runCurrent()
        assertTrue(started.isCompleted)
        screenScope.cancel()
        finishDelete.complete(Unit)
        advanceUntilIdle()

        assertEquals(listOf("cloud-1"), cloudCalls)
        assertEquals(1, coordinator.state.value.results.single().deletedCount)
        viewModelScope.cancel()
    }

    private fun mixedDelete() = PendingPhotoDelete(
        deviceMediaKeys = listOf("device-1"),
        cloudFileIds = listOf("cloud-ok", "cloud-fail"),
        totalCount = 3,
    )

    private fun coordinator(
        scope: CoroutineScope,
        deleteCloud: suspend (String) -> Unit,
    ) = GalleryDeletionCoordinator(
        scope = scope,
        deleteCloudFile = deleteCloud,
        onDeviceFilesDeleted = {},
        onCloudFileDeleted = {},
    )
}
