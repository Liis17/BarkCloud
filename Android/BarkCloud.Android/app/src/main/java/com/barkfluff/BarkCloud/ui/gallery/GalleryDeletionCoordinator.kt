package com.barkfluff.BarkCloud.ui.gallery

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

internal data class PendingPhotoDelete(
    val deviceMediaKeys: List<String>,
    val cloudFileIds: List<String>,
    val totalCount: Int,
    val accountReclaim: Boolean = false,
)

internal data class GalleryDeleteResult(
    val id: Int,
    val deletedCount: Int,
    val totalCount: Int,
    val accountReclaim: Boolean,
)

internal data class GalleryDeletionState(
    val isBusy: Boolean = false,
    val pendingConfirmation: PendingPhotoDelete? = null,
    val deletedCloudIds: Set<String> = emptySet(),
    val results: List<GalleryDeleteResult> = emptyList(),
)

internal class GalleryDeletionCoordinator(
    private val scope: CoroutineScope,
    private val deleteCloudFile: suspend (String) -> Unit,
    private val onDeviceFilesDeleted: (List<String>) -> Unit,
    private val onCloudFileDeleted: (String) -> Unit,
) {
    private val _state = MutableStateFlow(GalleryDeletionState())
    val state: StateFlow<GalleryDeletionState> = _state.asStateFlow()
    private var nextResultId = 0

    fun beginSystemConfirmation(pending: PendingPhotoDelete): Boolean {
        if (_state.value.isBusy) return false
        _state.update { it.copy(isBusy = true, pendingConfirmation = pending) }
        return true
    }

    fun abortSystemConfirmation() {
        if (_state.value.pendingConfirmation == null) return
        _state.update { it.copy(isBusy = false, pendingConfirmation = null) }
    }

    fun completeSystemConfirmation(pending: PendingPhotoDelete, approved: Boolean) {
        val current = _state.value
        if (current.pendingConfirmation != null && current.pendingConfirmation != pending) return
        if (current.pendingConfirmation == null && current.isBusy) return
        if (!approved) {
            _state.update { it.copy(isBusy = false, pendingConfirmation = null) }
            return
        }

        _state.update { it.copy(isBusy = true, pendingConfirmation = null) }
        execute(pending)
    }

    fun deleteCloudOnly(pending: PendingPhotoDelete): Boolean {
        if (pending.cloudFileIds.isEmpty() || _state.value.isBusy) return false
        _state.update { it.copy(isBusy = true, pendingConfirmation = null) }
        execute(pending.copy(deviceMediaKeys = emptyList(), cloudFileIds = pending.cloudFileIds.distinct()))
        return true
    }

    fun acknowledgeDeletedCloudIds(ids: Set<String>) {
        if (ids.isEmpty()) return
        _state.update { it.copy(deletedCloudIds = it.deletedCloudIds - ids) }
    }

    fun acknowledgeResult(id: Int) {
        _state.update { it.copy(results = it.results.filterNot { result -> result.id == id }) }
    }

    private fun execute(pending: PendingPhotoDelete) {
        scope.launch {
            var deletedCount = 0
            try {
                if (pending.deviceMediaKeys.isNotEmpty()) {
                    onDeviceFilesDeleted(pending.deviceMediaKeys)
                    deletedCount += pending.deviceMediaKeys.size
                }
                pending.cloudFileIds.distinct().forEach { fileId ->
                    try {
                        deleteCloudFile(fileId)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        return@forEach
                    }
                    deletedCount++
                    _state.update { it.copy(deletedCloudIds = it.deletedCloudIds + fileId) }
                    onCloudFileDeleted(fileId)
                }
            } catch (error: CancellationException) {
                _state.update { it.copy(isBusy = false, pendingConfirmation = null) }
                throw error
            }

            val result = GalleryDeleteResult(
                id = ++nextResultId,
                deletedCount = deletedCount,
                totalCount = pending.totalCount,
                accountReclaim = pending.accountReclaim,
            )
            _state.update {
                it.copy(
                    isBusy = false,
                    pendingConfirmation = null,
                    results = it.results + result,
                )
            }
        }
    }
}
