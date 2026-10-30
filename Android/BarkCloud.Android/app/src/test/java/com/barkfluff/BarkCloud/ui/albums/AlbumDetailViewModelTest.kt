package com.barkfluff.BarkCloud.ui.albums

import android.content.Context
import com.barkfluff.BarkCloud.data.cloud.AlbumItemsPage
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
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
class AlbumDetailViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val context = mockk<Context>(relaxed = true)
    private val repository = mockk<AlbumRepository>()
    private val cloudRepository = mockk<CloudRepository>()

    @Test fun `refresh waits for the active request without starting another`() = runTest {
        val response = CompletableDeferred<AlbumItemsPage>()
        coEvery { repository.listItems(any(), any(), any(), any(), any()) } coAnswers { response.await() }
        val vm = AlbumDetailViewModel(context, repository, cloudRepository)
        vm.start("album-1", "Отпуск")
        runCurrent()
        val refresh = async { vm.reloadAndWait() }
        runCurrent()
        assertFalse(refresh.isCompleted)
        coVerify(exactly = 1) { repository.listItems(any(), any(), any(), any(), any()) }

        response.complete(AlbumItemsPage(emptyList(), null, ""))
        runCurrent()
        assertTrue(refresh.isCompleted)
        assertFalse(vm.state.value.isLoading)
        assertFalse(vm.state.value.isRefreshing)
    }

    @Test fun `deleting a viewer file moves it to trash before removing it from album`() = runTest {
        val asset = MediaAsset("file-1", "photo.jpg", 10, CloudMediaKind.PHOTO, emptyList(), 1)
        coEvery { repository.listItems(any(), any(), any(), any(), any()) } returns
            AlbumItemsPage(listOf(asset), null, "")
        coEvery { cloudRepository.deleteUserMedia("file-1") } returns Unit
        val vm = AlbumDetailViewModel(context, repository, cloudRepository)
        vm.start("album-1", "Отпуск")
        runCurrent()

        vm.deleteFile("file-1")

        coVerify(exactly = 1) { cloudRepository.deleteUserMedia("file-1") }
        assertTrue(vm.state.value.items.isEmpty())
    }
}
