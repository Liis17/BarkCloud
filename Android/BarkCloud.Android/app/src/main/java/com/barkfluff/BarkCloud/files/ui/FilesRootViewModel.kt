package com.barkfluff.BarkCloud.files.ui

import androidx.lifecycle.ViewModel
import com.barkfluff.BarkCloud.files.data.StoragePermission
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class ServerFolder(val id: String, val name: String)

data class FilesRootUiState(
    val storageGranted: Boolean = false,
    val serverFolders: List<ServerFolder> = emptyList(),
)

class FilesRootViewModel : ViewModel() {

    private val _state = MutableStateFlow(FilesRootUiState())
    val state: StateFlow<FilesRootUiState> = _state.asStateFlow()

    fun refreshPermission() {
        _state.value = _state.value.copy(storageGranted = StoragePermission.isGranted())
    }

    val externalRootPath: String get() = StoragePermission.externalRoot.absolutePath
}
