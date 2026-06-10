package com.barkfluff.BarkCloud.files.ui

import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderCard
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRepository
import com.barkfluff.BarkCloud.data.cloud.DynamicFolderRule
import com.barkfluff.BarkCloud.files.data.StoragePermission
import barkcloud.files.FilesApiOuterClass.DfCombinator
import barkcloud.files.FilesApiOuterClass.DfViewMode
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ServerFolder(val id: String, val name: String)

data class FilesRootUiState(
    val storageGranted: Boolean = false,
    val serverFolders: List<ServerFolder> = emptyList(),
    val smartFolders: List<DynamicFolderCard> = emptyList(),
    val isLoadingSmartFolders: Boolean = false,
    val snackbar: String? = null,
)

class FilesRootViewModel(
    private val dynamicFolderRepository: DynamicFolderRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(FilesRootUiState())
    val state: StateFlow<FilesRootUiState> = _state.asStateFlow()

    fun refreshPermission() {
        _state.value = _state.value.copy(storageGranted = StoragePermission.isGranted())
    }

    fun loadSmartFolders() {
        if (_state.value.isLoadingSmartFolders) return
        _state.update { it.copy(isLoadingSmartFolders = true) }
        viewModelScope.launch {
            runCatching { dynamicFolderRepository.listFolders() }
                .onSuccess { folders ->
                    _state.update {
                        it.copy(isLoadingSmartFolders = false, smartFolders = folders)
                    }
                }
                .onFailure { error ->
                    _state.update {
                        it.copy(isLoadingSmartFolders = false, smartFolders = emptyList(), snackbar = error.message)
                    }
                }
        }
    }

    fun saveSmartFolder(
        existing: DynamicFolderCard?,
        name: String,
        combinator: DfCombinator,
        rules: List<DynamicFolderRule>,
        viewMode: DfViewMode,
    ) {
        val trimmed = name.trim()
        if (trimmed.isBlank() || rules.isEmpty()) return
        viewModelScope.launch {
            runCatching {
                if (existing == null) {
                    dynamicFolderRepository.create(trimmed, combinator, rules, viewMode)
                } else {
                    dynamicFolderRepository.update(existing.id, trimmed, combinator, rules, viewMode)
                }
            }
                .onSuccess { loadSmartFolders() }
                .onFailure { error -> _state.update { it.copy(snackbar = error.message) } }
        }
    }

    fun deleteSmartFolder(folder: DynamicFolderCard) {
        if (folder.isSystem) return
        viewModelScope.launch {
            runCatching { dynamicFolderRepository.delete(folder.id) }
                .onSuccess { loadSmartFolders() }
                .onFailure { error -> _state.update { it.copy(snackbar = error.message) } }
        }
    }

    fun snackbarShown() {
        _state.update { it.copy(snackbar = null) }
    }

    val externalRootPath: String get() = StoragePermission.externalRoot.absolutePath

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                    as BarkCloudApplication
                return FilesRootViewModel(app.dynamicFolderRepository) as T
            }
        }
    }
}
