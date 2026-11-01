package com.barkfluff.BarkCloud.data

import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout

/** Проверяет сохранённую сессию при запуске и каждом возвращении из фона. */
class SessionForegroundObserver(
    private val scope: CoroutineScope,
    private val refresh: suspend () -> Unit,
) : DefaultLifecycleObserver {
    private var refreshJob: Job? = null

    override fun onStart(owner: LifecycleOwner) {
        if (refreshJob?.isActive == true) return
        refreshJob = scope.launch {
            try {
                withTimeout(15_000) { refresh() }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                // При временной сетевой ошибке следующий запрос снова попробует refresh.
                // Недействительную сессию очищает TokenRefresher.
            }
        }
    }
}
