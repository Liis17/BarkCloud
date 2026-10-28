package com.barkfluff.BarkCloud.ui.components

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import barkcloud.files.FilesApiOuterClass.UserTagData
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Данные панели свойств. Каждый блок грузится и ошибается независимо. */
data class CloudMediaInfoUiState(
    val isLoading: Boolean = false,
    val metadataRows: List<Pair<String, String>> = emptyList(),
    val coordinates: Pair<Double, Double>? = null,
    val metadataError: String? = null,
    val alias: String = "",
    val tags: List<String> = emptyList(),
    val catalog: List<UserTagData> = emptyList(),
    val searchError: String? = null,
    val saving: Boolean = false,
    val saveError: String? = null,
)

/**
 * Панель свойств одного файла. [load] отменяет предыдущую загрузку, поэтому при листании
 * запросы не копятся. Черновик полей ввода хранится в UI, здесь только сохранённые значения.
 */
class CloudMediaInfoViewModel(
    private val repository: CloudRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(CloudMediaInfoUiState())
    val state: StateFlow<CloudMediaInfoUiState> = _state.asStateFlow()

    private var fileId: String = ""
    private var loadJob: Job? = null

    fun load(fileId: String) {
        this.fileId = fileId
        loadJob?.cancel()
        _state.value = CloudMediaInfoUiState(isLoading = true)
        loadJob = viewModelScope.launch {
            val metadata = runCatching { repository.fileMetadata(fileId) }
            val search = runCatching { repository.fileSearchMetadata(fileId) }
            val catalog = runCatching { repository.listUserTags() }
            // Отменённая загрузка не должна перезаписать состояние нового файла.
            ensureActive()
            val info = metadata.getOrNull()
            _state.value = CloudMediaInfoUiState(
                metadataRows = info?.let(::metadataRows).orEmpty(),
                coordinates = info?.let(::coordinatesOf),
                metadataError = metadata.exceptionOrNull()?.message,
                alias = search.getOrNull()?.alias.orEmpty(),
                tags = search.getOrNull()?.tagsList.orEmpty(),
                searchError = search.exceptionOrNull()?.message,
                catalog = catalog.getOrDefault(emptyList()),
            )
        }
    }

    /** Пустой [alias] удаляет алиас. Ввод пользователя не трогаем при ошибке. */
    fun save(alias: String, tags: List<String>) {
        val target = fileId
        viewModelScope.launch {
            _state.update { it.copy(saving = true, saveError = null) }
            try {
                val saved = repository.replaceFileSearchMetadata(target, alias.trim(), tags)
                _state.update { it.copy(saving = false, alias = saved.alias, tags = saved.tagsList) }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _state.update { it.copy(saving = false, saveError = e.message) }
            }
        }
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return CloudMediaInfoViewModel(app.cloudRepository) as T
            }
        }
    }
}
