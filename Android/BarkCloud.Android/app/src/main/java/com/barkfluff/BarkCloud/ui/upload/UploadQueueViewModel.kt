package com.barkfluff.BarkCloud.ui.upload

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.CreationExtras
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadScheduler
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class UploadQueueUiState(val jobs: List<UploadJob> = emptyList()) {
    val pendingJobs: List<UploadJob> = jobs
        .filter { it.phase != UploadPhase.COMPLETED && it.phase != UploadPhase.CANCELLED }
        .sortedWith(
            compareBy<UploadJob> { it.presentationGroup() }
                .thenBy { it.createdAtMillis }
                .thenBy { it.id },
        )
    val pendingErrorCount: Int = pendingJobs.count {
        it.phase == UploadPhase.FAILED || it.phase == UploadPhase.UPLOADED_NOT_ATTACHED
    }
    // Незавершённые задачи входят всегда: backup сохраняет исходный createdAtMillis, а длинная
    // очередь идёт дольше окна. Завершённые учитываются по времени завершения.
    private val sessionJobs: List<UploadJob>
        get() {
            val since = System.currentTimeMillis() - SESSION_WINDOW_MILLIS
            return jobs.filter {
                when (it.phase) {
                    in ACTIVE_PHASES -> true
                    UploadPhase.COMPLETED -> (it.completedAtMillis ?: it.createdAtMillis) >= since
                    else -> it.createdAtMillis >= since
                }
            }
        }
    val current: UploadJob? get() = sessionJobs.firstOrNull { it.phase in ACTIVE_PHASES }
    val total: Int get() = sessionJobs.size
    val completed: Int get() = sessionJobs.count { it.phase == UploadPhase.COMPLETED }
    val failed: Int get() = sessionJobs.count { it.phase == UploadPhase.FAILED }
    // Только задачи в работе: QUEUED backup, отложенный политикой сети, не крутит кольцо бесконечно.
    val isActive: Boolean get() = jobs.any { it.phase in IN_FLIGHT_PHASES }
    val progress: Float get() {
        val bytes = sessionJobs.sumOf { if (it.phase == UploadPhase.COMPLETED) it.bytesTotal else it.bytesSent }
        val totalBytes = sessionJobs.sumOf { it.bytesTotal }
        return if (totalBytes > 0) (bytes.toFloat() / totalBytes).coerceIn(0f, 1f) else 0f
    }

    private companion object {
        const val SESSION_WINDOW_MILLIS = 60L * 60L * 1000L
        val IN_FLIGHT_PHASES = setOf(
            UploadPhase.HASHING, UploadPhase.CREATING_SESSION,
            UploadPhase.UPLOADING, UploadPhase.COMPLETING, UploadPhase.PROCESSING, UploadPhase.ATTACHING,
        )
        val ACTIVE_PHASES = IN_FLIGHT_PHASES + UploadPhase.QUEUED
    }
}

private fun UploadJob.presentationGroup(): Int = when (phase) {
    UploadPhase.HASHING,
    UploadPhase.CREATING_SESSION,
    UploadPhase.UPLOADING,
    UploadPhase.COMPLETING,
    UploadPhase.PROCESSING,
    UploadPhase.ATTACHING -> 0
    UploadPhase.QUEUED -> 1
    else -> 2
}

class UploadQueueViewModel(private val app: BarkCloudApplication) : ViewModel() {
    private val _state = MutableStateFlow(UploadQueueUiState())
    val state: StateFlow<UploadQueueUiState> = _state.asStateFlow()

    init {
        viewModelScope.launch {
            app.uploadQueue.recentJobs.collectLatest { jobs -> _state.update { it.copy(jobs = jobs) } }
        }
    }

    fun retry(id: String) = viewModelScope.launch {
        app.uploadQueue.retry(id)
        UploadScheduler.enqueue(app)
    }

    fun cancel(id: String) = viewModelScope.launch { app.uploadQueue.cancel(id) }

    companion object {
        fun factory(): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T {
                val app = extras[ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY] as BarkCloudApplication
                return UploadQueueViewModel(app) as T
            }
        }
    }
}
