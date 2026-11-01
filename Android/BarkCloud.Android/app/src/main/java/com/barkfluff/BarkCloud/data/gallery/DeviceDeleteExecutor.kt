package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import kotlinx.coroutines.CancellationException

/** Фото, облачные копии которого не удалось удалить: сохраняется до повтора (счёт — по фото). */
internal data class PendingCloudCleanup(val mediaKey: String, val hash: String)

/** Хранилище незавершённых облачных очисток; реализация — SharedPreferences (см. [PendingCloudCleanupStore]). */
internal interface PendingCleanupRepository {
    fun all(): List<PendingCloudCleanup>
    fun add(entry: PendingCloudCleanup)
    fun remove(mediaKey: String)
}

internal data class DeviceDeleteReport(
    val devicePhotos: Int,
    val cloudCopiesDeleted: Int,
    /** Фото, у которых облачная очистка не завершилась; будут повторены. */
    val failedPhotos: Int,
)

/**
 * Выполняет подтверждённое удаление после ответа системы. Порядок важен: сначала отменяются
 * живые загрузки фото (чтобы staged-копия не «воскресила» файл), затем состояние облака
 * перепроверяется по хешу (учитывает копии, появившиеся во время подтверждения), и только потом
 * удаляются облачные копии. Ошибка одной копии не теряет остальные: фото попадает в ожидающие
 * очистки и повторяется, даже если локальный файл уже удалён.
 */
internal class DeviceDeleteExecutor(
    private val cloud: CloudRepository,
    private val queue: UploadQueueStore,
    private val mediaDao: MediaCloudStateDao,
    private val pending: PendingCleanupRepository,
) {
    /**
     * Вызывается после того, как система подтвердила удаление локальных копий [plan].
     * [hashes] — хеши фото, известные на момент подтверждения.
     */
    suspend fun finish(plan: DeviceDeletePlan, hashes: Map<String, String>): DeviceDeleteReport {
        for (mediaKey in plan.devicePhotos) {
            queue.cancelLiveForMediaKey(mediaKey)
            mediaDao.delete(mediaKey)
        }
        var cloudCopiesDeleted = 0
        var failedPhotos = 0
        for (mediaKey in plan.cloudCopies.keys) {
            val hash = hashes[mediaKey] ?: continue
            val result = deleteCloudCopies(mediaKey, hash)
            cloudCopiesDeleted += result.deleted
            if (!result.complete) failedPhotos++
        }
        return DeviceDeleteReport(plan.devicePhotos.size, cloudCopiesDeleted, failedPhotos)
    }

    /** Повтор незавершённых облачных очисток; завершённые снимаются, остальные остаются. */
    suspend fun retryPending(): Int {
        var failed = 0
        for (entry in pending.all()) {
            val result = deleteCloudCopies(entry.mediaKey, entry.hash)
            if (!result.complete) failed++
        }
        return failed
    }

    private data class CopyResult(val deleted: Int, val complete: Boolean)

    private suspend fun deleteCloudCopies(mediaKey: String, hash: String): CopyResult {
        val answer = try {
            cloud.checkFileHashesDetailed(listOf(hash))[hash]
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            null // сеть недоступна: состояние неизвестно, очистка повторится
        }
        val fresh = CloudCopy.of(answer)
        return when (fresh) {
            CloudCopy.Absent -> {
                pending.remove(mediaKey)
                CopyResult(0, complete = true)
            }
            CloudCopy.Unknown -> {
                pending.add(PendingCloudCleanup(mediaKey, hash))
                CopyResult(0, complete = false)
            }
            is CloudCopy.Present -> {
                var deleted = 0
                var failed = false
                for (fileId in fresh.fileIds) {
                    try {
                        cloud.deleteUserMedia(fileId)
                        deleted++
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        failed = true
                    }
                }
                if (failed) pending.add(PendingCloudCleanup(mediaKey, hash)) else pending.remove(mediaKey)
                CopyResult(deleted, complete = !failed)
            }
        }
    }
}
