package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy

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
