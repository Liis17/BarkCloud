package com.barkfluff.BarkCloud.ui.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.update.AndroidUpdateRepository
import java.io.File
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class AndroidUpdatesViewModel(private val repository: AndroidUpdateRepository) : ViewModel() {
    val state = repository.state
    private val _actionError = MutableStateFlow<String?>(null)
    val actionError: StateFlow<String?> = _actionError.asStateFlow()

    fun onResume() {
        runAction { repository.checkForPage(force = false) }
    }

    fun manualRefresh() = runAction { repository.checkForPage(force = true) }
    fun download() = runAction { repository.startDownload() }
    fun cancelDownload() = runAction { repository.cancelDownload() }
    fun deleteDownloadedUpdate() = runAction { repository.deleteDownloadedUpdate() }
    suspend fun reconcileNow() = repository.reconcile()

    suspend fun prepareInstall(): File = repository.prepareInstall()

    fun clearActionError() {
        _actionError.value = null
    }

    fun reportActionError(message: String) {
        _actionError.value = message
    }

    private fun runAction(action: suspend () -> Unit) {
        viewModelScope.launch {
            _actionError.value = null
            try {
                action()
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                _actionError.value = error.message ?: "Не удалось выполнить действие."
            }
        }
    }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY] as BarkCloudApplication
                return AndroidUpdatesViewModel(app.androidUpdateRepository) as T
            }
        }
    }
}
