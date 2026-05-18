package com.barkfluff.BarkCloud.files.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.files.data.LocalFileRepository
import com.barkfluff.BarkCloud.files.data.StoragePermission
import com.barkfluff.BarkCloud.files.domain.FsEntry
import com.barkfluff.BarkCloud.files.domain.FsSort
import com.barkfluff.BarkCloud.files.domain.applySort
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File

enum class PendingOpKind { Copy, Move, Delete }

data class PendingOp(val kind: PendingOpKind, val progress: Float)

data class BrowserUiState(
    val currentPath: String = "",
    val rootPath: String = "",
    val title: String = "",
    val entries: List<FsEntry> = emptyList(),
    val sort: FsSort = FsSort.NameAsc,
    val showHidden: Boolean = false,
    val selection: Set<String> = emptySet(),
    val isLoading: Boolean = false,
    val pendingOp: PendingOp? = null,
) {
    val selectionActive: Boolean get() = selection.isNotEmpty()
    val canGoUp: Boolean get() = currentPath != rootPath
}

class LocalBrowserViewModel(
    private val repository: LocalFileRepository,
    initialPath: String,
    private val rootLabel: String,
) : ViewModel() {

    private val rootPath: String = StoragePermission.externalRoot.absolutePath

    private val _state = MutableStateFlow(
        BrowserUiState(
            currentPath = initialPath,
            rootPath = rootPath,
            title = labelFor(initialPath),
        ),
    )
    val state: StateFlow<BrowserUiState> = _state.asStateFlow()

    sealed class BrowserEvent {
        data class Toast(val text: String) : BrowserEvent()
        data class OpenFile(val file: java.io.File, val mime: String) : BrowserEvent()
        data class ShareFiles(val files: List<java.io.File>) : BrowserEvent()
    }

    private val _events = Channel<BrowserEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    init {
        refresh()
    }

    private fun labelFor(path: String): String = when {
        path == rootPath -> rootLabel
        else -> File(path).name
    }

    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(isLoading = true) }
            val current = _state.value
            val result = repository.list(current.currentPath, current.showHidden)
            val sorted = result.getOrDefault(emptyList()).applySort(current.sort)
            _state.update {
                it.copy(
                    isLoading = false,
                    entries = sorted,
                )
            }
            result.exceptionOrNull()?.localizedMessage?.let { msg ->
                _events.send(BrowserEvent.Toast(msg))
            }
        }
    }

    fun enter(directory: FsEntry.Directory) {
        _state.update {
            it.copy(
                currentPath = directory.path,
                title = labelFor(directory.path),
                selection = emptySet(),
                entries = emptyList(),
            )
        }
        refresh()
    }

    fun goUp(): Boolean {
        val state = _state.value
        if (!state.canGoUp) return false
        val parent = File(state.currentPath).parentFile?.absolutePath ?: return false
        _state.update {
            it.copy(
                currentPath = parent,
                title = labelFor(parent),
                selection = emptySet(),
                entries = emptyList(),
            )
        }
        refresh()
        return true
    }

    fun toggleSelect(path: String) {
        _state.update {
            val newSel = it.selection.toMutableSet().apply {
                if (!add(path)) remove(path)
            }
            it.copy(selection = newSel)
        }
    }

    fun selectAll() {
        _state.update { it.copy(selection = it.entries.map { e -> e.path }.toSet()) }
    }

    fun clearSelection() {
        _state.update { it.copy(selection = emptySet()) }
    }

    fun setSort(sort: FsSort) {
        _state.update {
            it.copy(
                sort = sort,
                entries = it.entries.applySort(sort),
            )
        }
    }

    fun toggleHidden() {
        _state.update { it.copy(showHidden = !it.showHidden) }
        refresh()
    }

    fun selectedEntries(): List<FsEntry> {
        val state = _state.value
        val sel = state.selection
        return state.entries.filter { it.path in sel }
    }

    fun createFolder(name: String) {
        viewModelScope.launch {
            val current = _state.value.currentPath
            val result = repository.createDir(current, name)
            result.exceptionOrNull()?.localizedMessage?.let { _events.send(BrowserEvent.Toast(it)) }
            refresh()
        }
    }

    fun rename(entry: FsEntry, newName: String) {
        viewModelScope.launch {
            val result = repository.rename(entry, newName)
            result.exceptionOrNull()?.localizedMessage?.let { _events.send(BrowserEvent.Toast(it)) }
            clearSelection()
            refresh()
        }
    }

    fun deleteSelected() {
        viewModelScope.launch {
            val entries = selectedEntries()
            if (entries.isEmpty()) return@launch
            _state.update { it.copy(pendingOp = PendingOp(PendingOpKind.Delete, 0f)) }
            val result = repository.delete(entries)
            _state.update { it.copy(pendingOp = null, selection = emptySet()) }
            result.exceptionOrNull()?.localizedMessage?.let { _events.send(BrowserEvent.Toast(it)) }
            refresh()
        }
    }

    fun copySelectedTo(targetPath: String) {
        viewModelScope.launch {
            val entries = selectedEntries()
            if (entries.isEmpty()) return@launch
            _state.update { it.copy(pendingOp = PendingOp(PendingOpKind.Copy, 0f)) }
            val result = repository.copy(entries, targetPath) { progress ->
                _state.update {
                    val cur = it.pendingOp ?: return@update it
                    it.copy(pendingOp = cur.copy(progress = progress))
                }
            }
            _state.update { it.copy(pendingOp = null, selection = emptySet()) }
            result.exceptionOrNull()?.localizedMessage?.let { _events.send(BrowserEvent.Toast(it)) }
            refresh()
        }
    }

    fun moveSelectedTo(targetPath: String) {
        viewModelScope.launch {
            val entries = selectedEntries()
            if (entries.isEmpty()) return@launch
            _state.update { it.copy(pendingOp = PendingOp(PendingOpKind.Move, 0f)) }
            val result = repository.move(entries, targetPath) { progress ->
                _state.update {
                    val cur = it.pendingOp ?: return@update it
                    it.copy(pendingOp = cur.copy(progress = progress))
                }
            }
            _state.update { it.copy(pendingOp = null, selection = emptySet()) }
            result.exceptionOrNull()?.localizedMessage?.let { _events.send(BrowserEvent.Toast(it)) }
            refresh()
        }
    }

    fun openFile(entry: FsEntry.File) {
        viewModelScope.launch {
            _events.send(BrowserEvent.OpenFile(File(entry.path), entry.mimeType))
        }
    }

    fun shareFiles(entries: List<FsEntry.File>) {
        if (entries.isEmpty()) return
        viewModelScope.launch {
            _events.send(BrowserEvent.ShareFiles(entries.map { File(it.path) }))
        }
    }

    companion object {
        fun factory(initialPath: String, rootLabel: String): ViewModelProvider.Factory =
            object : ViewModelProvider.Factory {
                @Suppress("UNCHECKED_CAST")
                override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                    val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]
                        as BarkCloudApplication
                    return LocalBrowserViewModel(
                        repository = app.localFileRepository,
                        initialPath = initialPath,
                        rootLabel = rootLabel,
                    ) as T
                }
            }
    }
}
