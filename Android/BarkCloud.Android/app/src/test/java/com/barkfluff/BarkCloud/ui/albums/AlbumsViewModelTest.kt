package com.barkfluff.BarkCloud.ui.albums

import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.cloud.AlbumPage
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class AlbumsViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val repository = mockk<AlbumRepository>()

    @Test fun `refresh waits for the active request without starting another`() = runTest {
        val response = CompletableDeferred<AlbumPage>()
        coEvery { repository.listAlbums(any(), any(), any()) } coAnswers { response.await() }
        val vm = AlbumsViewModel(repository)
        vm.reload()
        runCurrent()
        val refresh = async { vm.reloadAndWait() }
        runCurrent()
        assertFalse(refresh.isCompleted)
        coVerify(exactly = 1) { repository.listAlbums(any(), any(), any()) }

        response.complete(AlbumPage(emptyList(), null, ""))
        runCurrent()
        assertTrue(refresh.isCompleted)
        assertFalse(vm.state.value.isLoading)
    }

    @Test fun `failed refresh preserves existing albums and ends loading`() = runTest {
        val album = mockk<AlbumCard>()
        coEvery { repository.listAlbums(any(), any(), any()) } returns AlbumPage(listOf(album), null, "")
        val vm = AlbumsViewModel(repository)
        vm.reloadAndWait()
        coEvery { repository.listAlbums(any(), any(), any()) } throws IllegalStateException("offline")

        vm.reloadAndWait()

        assertEquals(listOf(album), vm.state.value.albums)
        assertEquals("offline", vm.state.value.snackbar)
        assertFalse(vm.state.value.isRefreshing)
        assertFalse(vm.state.value.isLoading)
    }
}
