package com.barkfluff.BarkCloud.ui.search

import barkcloud.files.FilesApiOuterClass.SearchHit
import barkcloud.files.FilesApiOuterClass.SearchSection
import barkcloud.files.FilesApiOuterClass.SearchSectionResult
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.support.MainDispatcherRule
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class SearchViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val repository = mockk<CloudRepository>()

    @Test fun `typing is debounced and short queries clear results without requests`() = runTest {
        coEvery { repository.search(any(), any()) } returns listOf(section("one"))
        val vm = SearchViewModel(repository)
        vm.setQuery("ca")
        advanceTimeBy(200)
        vm.setQuery("cats")
        advanceUntilIdle()
        coVerify(exactly = 0) { repository.search("ca", any()) }
        coVerify(exactly = 1) { repository.search("cats", any()) }
        assertEquals(1, vm.state.value.sections.size)
        vm.setQuery("c")
        advanceUntilIdle()
        assertTrue(vm.state.value.sections.isEmpty())
        assertFalse(vm.state.value.isLoading)
        coVerify(exactly = 0) { repository.search("c", any()) }
    }

    @Test fun `late response from cancelled query cannot replace current results`() = runTest {
        coEvery { repository.search("old", any()) } coAnswers {
            withContext(NonCancellable) { delay(1000) }
            listOf(section("old"))
        }
        coEvery { repository.search("new", any()) } returns listOf(section("new"))
        val vm = SearchViewModel(repository)
        vm.setQuery("old")
        advanceTimeBy(350)
        runCurrent()
        vm.setQuery("new")
        advanceUntilIdle()
        assertEquals("new", vm.state.value.sections.single().hitsList.single().id)
        assertNull(vm.state.value.error)
    }

    @Test fun `next page preserves other sections and existing hits after failure and retry`() = runTest {
        val first = section("one", hasMore = true)
        val album = SearchSectionResult.newBuilder().setSection(SearchSection.SEARCH_SECTION_ALBUMS)
            .addHits(SearchHit.newBuilder().setId("album")).build()
        coEvery { repository.search(any(), any()) } returns listOf(first, album)
        val vm = SearchViewModel(repository)
        vm.setQuery("cats")
        advanceUntilIdle()
        coEvery { repository.search(any(), match { it.singleOrNull()?.cursor == "next" }) } throws IllegalStateException("offline")
        vm.loadMore(first)
        advanceUntilIdle()
        assertEquals("offline", vm.state.value.error)
        assertEquals(listOf(first, album), vm.state.value.sections)
        coEvery { repository.search(any(), match { it.singleOrNull()?.cursor == "next" }) } returns listOf(section("two"))
        vm.loadMore(first)
        advanceUntilIdle()
        assertEquals(listOf("one", "two"), vm.state.value.sections.first().hitsList.map { it.id })
        assertEquals(album, vm.state.value.sections.last())
        assertNull(vm.state.value.error)
        assertFalse(vm.state.value.sections.first().hasMore)
    }

    private fun section(id: String, hasMore: Boolean = false): SearchSectionResult =
        SearchSectionResult.newBuilder().setSection(SearchSection.SEARCH_SECTION_FILES)
            .addHits(SearchHit.newBuilder().setId(id)).setHasMore(hasMore)
            .setNextCursor(if (hasMore) "next" else "").build()
}
