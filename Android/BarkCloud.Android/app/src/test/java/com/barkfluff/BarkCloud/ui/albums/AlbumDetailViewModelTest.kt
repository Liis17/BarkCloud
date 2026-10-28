package com.barkfluff.BarkCloud.ui.albums

import android.content.Context
import com.barkfluff.BarkCloud.data.cloud.AlbumItemsPage
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
class AlbumDetailViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val context = mockk<Context>(relaxed = true)
    private val repository = mockk<AlbumRepository>()

    @Test fun `refresh waits for the active request without starting another`() = runTest {
        val response = CompletableDeferred<AlbumItemsPage>()
        coEvery { repository.listItems(any(), any(), any(), any(), any()) } coAnswers { response.await() }
        val vm = AlbumDetailViewModel(context, repository)
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
}
