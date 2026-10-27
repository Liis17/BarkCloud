package com.barkfluff.BarkCloud.ui.search

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import barkcloud.files.FilesApiOuterClass.SearchSection
import barkcloud.files.FilesApiOuterClass.SearchSectionPage
import barkcloud.files.FilesApiOuterClass.SearchSectionResult
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class SearchUiState(
    val query: String = "",
    val isLoading: Boolean = false,
    val sections: List<SearchSectionResult> = emptyList(),
    val error: String? = null,
)

class SearchViewModel(private val repository: CloudRepository) : ViewModel() {
    private val _state = MutableStateFlow(SearchUiState())
    val state = _state.asStateFlow()
    private var searchJob: Job? = null

    fun setQuery(query: String) {
        searchJob?.cancel()
        _state.value = SearchUiState(query = query, isLoading = query.trim().length >= 2)
        if (!_state.value.isLoading) return
        searchJob = viewModelScope.launch {
            delay(350)
            fetch(query.trim(), sections.map { page(it) })
        }
    }

    fun loadMore(section: SearchSectionResult) {
        if (_state.value.isLoading || !section.hasMore) return
        _state.update { it.copy(isLoading = true, error = null) }
        searchJob = viewModelScope.launch {
            fetch(_state.value.query.trim(), listOf(page(section.section, section.nextCursor)), append = true)
        }
    }

    private suspend fun fetch(query: String, pages: List<SearchSectionPage>, append: Boolean = false) {
        try {
            val result = repository.search(query, pages)
            currentCoroutineContext().ensureActive()
            _state.update { state ->
                val merged = if (!append) result else state.sections.map { previous ->
                    result.find { it.section == previous.section }?.let { next ->
                        next.toBuilder().clearHits().addAllHits(
                            (previous.hitsList + next.hitsList).distinctBy { it.kind to it.id },
                        ).build()
                    } ?: previous
                }
                state.copy(isLoading = false, sections = merged, error = null)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            _state.update { it.copy(isLoading = false, error = e.message ?: "Не удалось выполнить поиск") }
        }
    }

    companion object {
        private val sections = listOf(
            SearchSection.SEARCH_SECTION_PHOTOS,
            SearchSection.SEARCH_SECTION_VIDEOS,
            SearchSection.SEARCH_SECTION_FILES,
            SearchSection.SEARCH_SECTION_ALBUMS,
            SearchSection.SEARCH_SECTION_FOLDERS,
        )

        private fun page(section: SearchSection, cursor: String = ""): SearchSectionPage =
            SearchSectionPage.newBuilder().setSection(section).setLimit(20).setCursor(cursor).build()

        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY] as BarkCloudApplication
                return SearchViewModel(app.cloudRepository) as T
            }
        }
    }
}
