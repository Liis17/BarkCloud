package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collectLatest

internal suspend fun restoreActiveUploadQueueAtStartup(
    policy: AutoUploadNetworkPolicy,
    initializeQueue: suspend () -> Unit,
    activeCount: suspend () -> Int,
    activeNonBackupCount: suspend () -> Int,
    enqueue: () -> Unit,
) {
    initializeQueue()
    val active = activeCount()
    if (active > 0 && (policy != AutoUploadNetworkPolicy.OFF || activeNonBackupCount() > 0)) {
        enqueue()
    }
}

internal suspend fun observeUploadStartupForSession(
    sessionActive: StateFlow<Boolean>,
    readPolicy: () -> AutoUploadNetworkPolicy,
    initializeQueue: suspend () -> Unit,
    activeCount: suspend () -> Int,
    activeNonBackupCount: suspend () -> Int,
    enqueue: () -> Unit,
    applyPolicy: (AutoUploadNetworkPolicy) -> Unit,
) {
    sessionActive.collectLatest { active ->
        if (!active) return@collectLatest
        val startupContext = currentCoroutineContext()
        val policy = readPolicy()
        restoreActiveUploadQueueAtStartup(
            policy = policy,
            initializeQueue = initializeQueue,
            activeCount = activeCount,
            activeNonBackupCount = activeNonBackupCount,
            enqueue = {
                startupContext.ensureActive()
                if (sessionActive.value) enqueue()
            },
        )
        startupContext.ensureActive()
        if (sessionActive.value) applyPolicy(policy)
    }
}
