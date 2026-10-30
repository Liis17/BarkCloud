package com.barkfluff.BarkCloud.data.gallery

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

internal fun interface BackupUploadQueue {
    suspend fun enqueueBackupIfAbsent(media: DeviceMedia): Boolean
}

/** Serializes scans and only hands bounded MediaStore batches to the persistent queue. */
internal class BackupQueueDiscovery(
    private val queue: BackupUploadQueue,
    private val isEnabled: () -> Boolean,
    private val includesFolder: (String) -> Boolean,
) {
    suspend fun discover(readBatches: suspend (suspend (List<DeviceMedia>) -> Unit) -> Unit): Int =
        scanMutex.withLock {
            var queued = 0
            readBatches { batch ->
                if (isEnabled()) {
                    for (media in batch) {
                        if (!isEnabled()) break
                        if (includesFolder(media.folderKey) && queue.enqueueBackupIfAbsent(media)) queued++
                    }
                }
            }
            queued
        }

    private companion object {
        val scanMutex = Mutex()
    }
}

internal suspend fun discoverAndWakeUploadQueue(
    discover: suspend () -> Int,
    activeCount: suspend () -> Int,
    enqueueUpload: () -> Unit,
): Int {
    val discovered = discover()
    if (activeCount() > 0) enqueueUpload()
    return discovered
}
