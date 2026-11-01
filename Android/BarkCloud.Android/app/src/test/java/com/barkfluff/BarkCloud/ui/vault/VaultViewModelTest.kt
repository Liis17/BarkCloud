package com.barkfluff.BarkCloud.ui.vault

import android.content.Context
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.vault.VaultItem
import com.barkfluff.BarkCloud.data.vault.VaultStore
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.verify
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import java.io.IOException

class VaultViewModelTest {

    @get:Rule
    val main = MainDispatcherRule()

    private val items = listOf("a", "b", "c").map { VaultItem(it, "$it.jpg", null, 0, false) }
    private val store = mockk<VaultStore>(relaxed = true)
    private val cloud = mockk<CloudRepository>(relaxed = true)
    private val vm = VaultViewModel(mockk<Context>(relaxed = true), store, mockk<AlbumRepository>(relaxed = true), cloud)

    private fun openWithSelectionOfAll() {
        every { store.items() } returns items
        val generation = checkNotNull(vm.onUnlockStarted())
        vm.onUnlockResult(generation, ok = true)
        vm.setSelection(setOf("a", "b", "c"))
    }

    @Test
    fun relockDuringFirstDeletionStopsBatchBeforeNextItem() = runTest {
        openWithSelectionOfAll()
        val firstGate = CompletableDeferred<Unit>()
        coEvery { cloud.deleteUserMedia("a") } coAnswers { firstGate.await() }

        vm.deleteSelected()
        runCurrent()
        vm.relock()
        firstGate.complete(Unit)
        advanceUntilIdle()

        coVerify(exactly = 1) { cloud.deleteUserMedia(any()) }
        coVerify(exactly = 0) { cloud.deleteUserMedia("b") }
        coVerify(exactly = 0) { cloud.deleteUserMedia("c") }
        assertEquals(VaultLockState.LOCKED, vm.state.value.lockState)
        assertEquals(emptySet<String>(), vm.state.value.selected)
    }

    @Test
    fun succeededDeletionIsReconciledEvenAfterRelock() = runTest {
        openWithSelectionOfAll()
        val firstGate = CompletableDeferred<Unit>()
        coEvery { cloud.deleteUserMedia("a") } coAnswers { firstGate.await() }

        vm.deleteSelected()
        runCurrent()
        vm.relock()
        firstGate.complete(Unit)
        advanceUntilIdle()

        verify { store.remove("a") }
    }

    @Test
    fun staleUnlockResultAfterRelockDoesNotResurrectVault() = runTest {
        every { store.items() } returns items
        val staleGeneration = checkNotNull(vm.onUnlockStarted())
        vm.relock()

        vm.onUnlockResult(staleGeneration, ok = true)

        assertEquals(VaultLockState.LOCKED, vm.state.value.lockState)
    }

    @Test
    fun unfinishedTargetsReturnToSelectionOnlyAfterNewUnlock() = runTest {
        openWithSelectionOfAll()
        val firstGate = CompletableDeferred<Unit>()
        coEvery { cloud.deleteUserMedia("a") } coAnswers { firstGate.await() }
        vm.deleteSelected()
        runCurrent()
        vm.relock()
        firstGate.complete(Unit)
        advanceUntilIdle()
        // Пока заблокировано, отложенный выбор не показывается.
        assertEquals(emptySet<String>(), vm.state.value.selected)

        val generation = checkNotNull(vm.onUnlockStarted())
        vm.onUnlockResult(generation, ok = true)

        assertEquals(setOf("b", "c"), vm.state.value.selected)
    }

    @Test
    fun failedDeletionRemainsSelectedForRetry() = runTest {
        openWithSelectionOfAll()
        coEvery { cloud.deleteUserMedia("b") } throws IOException("network")

        vm.deleteSelected()
        advanceUntilIdle()

        assertEquals(setOf("b"), vm.state.value.selected)
        assertEquals(listOf("b"), vm.state.value.items.map { it.fileId })
    }

    @Test
    fun failedAndUnprocessedIdsStayHiddenUntilVaultIsUnlockedAgain() = runTest {
        openWithSelectionOfAll()
        val failedGate = CompletableDeferred<Unit>()
        coEvery { cloud.deleteUserMedia("b") } coAnswers {
            failedGate.await()
            throw IOException("network")
        }

        vm.deleteSelected()
        runCurrent()
        vm.relock()
        failedGate.complete(Unit)
        advanceUntilIdle()

        assertEquals(VaultLockState.LOCKED, vm.state.value.lockState)
        assertEquals(emptySet<String>(), vm.state.value.selected)

        val generation = checkNotNull(vm.onUnlockStarted())
        vm.onUnlockResult(generation, ok = true)

        assertEquals(setOf("b", "c"), vm.state.value.selected)
    }
}
