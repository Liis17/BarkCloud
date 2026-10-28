package com.barkfluff.BarkCloud.files.ui

import com.barkfluff.BarkCloud.data.cloud.DynamicFolderCard
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRepository
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.mockk
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class FilesRootViewModelTest {
    @get:Rule val main = MainDispatcherRule()

    @Test fun `failed refresh preserves existing smart folders`() = runTest {
        val repository = mockk<DynamicFolderRepository>()
        val folder = mockk<DynamicFolderCard>()
        coEvery { repository.listFolders() } returns listOf(folder)
        val vm = FilesRootViewModel(repository)
        vm.loadSmartFolders()
        advanceUntilIdle()
        coEvery { repository.listFolders() } throws IllegalStateException("offline")

        vm.loadSmartFolders()
        advanceUntilIdle()

        assertEquals(listOf(folder), vm.state.value.smartFolders)
        assertEquals("offline", vm.state.value.snackbar)
        assertFalse(vm.state.value.isLoadingSmartFolders)
    }
}
