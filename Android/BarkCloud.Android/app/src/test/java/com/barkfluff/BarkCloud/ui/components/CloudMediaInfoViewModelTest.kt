package com.barkfluff.BarkCloud.ui.components

import barkcloud.files.FilesApiOuterClass.FileSearchMetadata
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.CompletableDeferred
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

    @Test fun `delayed successful save for previous file does not replace current file state`() = runTest {
        val pendingSave = CompletableDeferred<FileSearchMetadata>()
        val currentFileMetadata = FileSearchMetadata.newBuilder()
            .setAlias("Альбом B")
            .addTags("тег B")
            .build()
        coEvery { repository.fileMetadata(any()) } returns null
        coEvery { repository.fileSearchMetadata(any()) } returns FileSearchMetadata.getDefaultInstance()
        coEvery { repository.fileSearchMetadata("B") } returns currentFileMetadata
        coEvery { repository.listUserTags() } returns emptyList()
        coEvery { repository.replaceFileSearchMetadata("A", "Альбом A новый", listOf("тег A")) } coAnswers {
            pendingSave.await()
        }
        val vm = CloudMediaInfoViewModel(repository)

        vm.load("A")
        runCurrent()
        vm.save("Альбом A новый", listOf("тег A"))
        runCurrent()
        vm.load("B")
        runCurrent()
        pendingSave.complete(
            FileSearchMetadata.newBuilder().setAlias("Альбом A новый").addTags("тег A").build(),
        )
        runCurrent()

        assertEquals("Альбом B", vm.state.value.alias)
        assertEquals(listOf("тег B"), vm.state.value.tags)
        assertNull(vm.state.value.saveError)
        assertFalse(vm.state.value.saving)
    }

    @Test fun `delayed failed save for previous file does not report error on current file`() = runTest {
        val pendingSave = CompletableDeferred<FileSearchMetadata>()
        val currentFileMetadata = FileSearchMetadata.newBuilder()
            .setAlias("Альбом B")
            .addTags("тег B")
            .build()
        coEvery { repository.fileMetadata(any()) } returns null
        coEvery { repository.fileSearchMetadata(any()) } returns FileSearchMetadata.getDefaultInstance()
        coEvery { repository.fileSearchMetadata("B") } returns currentFileMetadata
        coEvery { repository.listUserTags() } returns emptyList()
        coEvery { repository.replaceFileSearchMetadata("A", "Альбом A новый", listOf("тег A")) } coAnswers {
            pendingSave.await()
        }
        val vm = CloudMediaInfoViewModel(repository)

        vm.load("A")
        runCurrent()
        vm.save("Альбом A новый", listOf("тег A"))
        runCurrent()
        vm.load("B")
        runCurrent()
        pendingSave.completeExceptionally(IllegalStateException("сеть"))
        runCurrent()

        assertEquals("Альбом B", vm.state.value.alias)
        assertEquals(listOf("тег B"), vm.state.value.tags)
        assertNull(vm.state.value.saveError)
        assertFalse(vm.state.value.saving)
    }
}
