package com.barkfluff.BarkCloud.ui.components

import barkcloud.files.FilesApiOuterClass.FileSearchMetadata
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CloudMediaInfoViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val repository = mockk<CloudRepository>()

    @Test fun `file without metadata shows no rows and no error`() = runTest {
        coEvery { repository.fileMetadata("f1") } returns null
        coEvery { repository.fileSearchMetadata("f1") } returns FileSearchMetadata.getDefaultInstance()
        coEvery { repository.listUserTags() } returns emptyList()
        val vm = CloudMediaInfoViewModel(repository)

        vm.load("f1")
        runCurrent()

        val state = vm.state.value
        assertTrue(state.metadataRows.isEmpty())
        assertNull(state.coordinates)
        assertNull(state.metadataError)
        assertFalse(state.isLoading)
    }

    @Test fun `save trims alias and sends tags`() = runTest {
        coEvery { repository.fileMetadata(any()) } returns null
        coEvery { repository.fileSearchMetadata(any()) } returns FileSearchMetadata.getDefaultInstance()
        coEvery { repository.listUserTags() } returns emptyList()
        coEvery { repository.replaceFileSearchMetadata("f1", "Паспорт", listOf("важное")) } returns
            FileSearchMetadata.newBuilder().setAlias("Паспорт").addTags("важное").build()
        val vm = CloudMediaInfoViewModel(repository)
        vm.load("f1")
        runCurrent()

        vm.save("  Паспорт  ", listOf("важное"))
        runCurrent()

        coVerify(exactly = 1) { repository.replaceFileSearchMetadata("f1", "Паспорт", listOf("важное")) }
        assertEquals("Паспорт", vm.state.value.alias)
        assertEquals(listOf("важное"), vm.state.value.tags)
        assertFalse(vm.state.value.saving)
    }

    @Test fun `failed save keeps previously saved values and reports error`() = runTest {
        coEvery { repository.fileMetadata(any()) } returns null
        coEvery { repository.fileSearchMetadata(any()) } returns
            FileSearchMetadata.newBuilder().setAlias("старое").build()
        coEvery { repository.listUserTags() } returns emptyList()
        coEvery { repository.replaceFileSearchMetadata(any(), any(), any()) } throws RuntimeException("сеть")
        val vm = CloudMediaInfoViewModel(repository)
        vm.load("f1")
        runCurrent()

        vm.save("новое", emptyList())
        runCurrent()

        assertEquals("старое", vm.state.value.alias)
        assertEquals("сеть", vm.state.value.saveError)
        assertFalse(vm.state.value.saving)
    }
}
