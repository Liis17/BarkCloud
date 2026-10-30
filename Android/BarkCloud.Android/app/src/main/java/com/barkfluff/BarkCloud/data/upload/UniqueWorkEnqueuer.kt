package com.barkfluff.BarkCloud.data.upload

import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequest
import androidx.work.WorkInfo
import androidx.work.WorkManager
import java.util.concurrent.Executor

/** Coalesces an enqueued request and appends one successor when the worker is already running. */
internal object UniqueWorkEnqueuer {
    private data class Pending(val workManager: WorkManager, var request: OneTimeWorkRequest, var recheck: Boolean = false)

    private val lock = Any()
    private val pending = mutableMapOf<String, Pending>()
    private val directExecutor = Executor { it.run() }

    fun enqueue(workManager: WorkManager, name: String, request: OneTimeWorkRequest) {
        val startQuery = synchronized(lock) {
            val entry = pending[name]
            if (entry != null) {
                entry.request = request
                entry.recheck = true
                false
            } else {
                pending[name] = Pending(workManager, request)
                true
            }
        }
        if (startQuery) query(name)
    }

    fun cancelPending(name: String) {
        synchronized(lock) { pending.remove(name) }
    }

    private fun query(name: String) {
        val workManager = synchronized(lock) { pending[name]?.workManager } ?: return
        val query = workManager.getWorkInfosForUniqueWork(name)
        query.addListener({
            val infos = runCatching { query.get() }.getOrNull()
            val repeat = synchronized(lock) {
                val entry = pending[name] ?: return@addListener
                if (entry.recheck) {
                    entry.recheck = false
                    true
                } else {
                    pending.remove(name)
                    val policy = if (infos == null) {
                        ExistingWorkPolicy.APPEND_OR_REPLACE
                    } else {
                        policyFor(infos.map { it.state })
                    }
                    if (policy != null) workManager.enqueueUniqueWork(name, policy, entry.request)
                    false
                }
            }
            if (repeat) query(name)
        }, directExecutor)
    }

    internal fun policyFor(states: List<WorkInfo.State>): ExistingWorkPolicy? = when {
        states.any { it == WorkInfo.State.ENQUEUED } -> null
        states.any { it == WorkInfo.State.RUNNING } -> ExistingWorkPolicy.APPEND_OR_REPLACE
        else -> ExistingWorkPolicy.KEEP
    }
}
