package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cache.CacheScope
import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import kotlinx.coroutines.CancellationException
import java.util.UUID

/** Облако: состояние копии по хешу (null — ответ недоступен) и удаление одного файла. */
internal interface DeleteCloudPort {
    suspend fun copyState(hash: String): CloudCopy?
    suspend fun deleteFile(fileId: String)
}

/** Локальные копии: отмена живых загрузок и забывание состояния фото после подтверждённого удаления. */
internal interface DeviceCopyPort {
    suspend fun cancelLiveJobs(mediaKey: String)
    suspend fun forget(mediaKey: String)
}

/** Vault на клиенте: ID скрытых файлов и удаление записи после подтверждённого удаления в облаке. */
internal interface VaultPort {
    fun fileIds(): Set<String>
    fun remove(fileId: String)
}

internal sealed interface CloudRunResult {
    data class Finished(
        val deleted: Int,
        val reconciled: Int,
        /** Новые копии, найденные после подтверждения: не удаляются, нужен новый подтверждённый план. */
        val newCopies: Int,
        val failed: Int,
        val remaining: Int,
    ) : CloudRunResult

    /** Осталась скрытая цель, а разблокировки текущей сессии нет: остальные ID не тронуты. */
    data class NeedsUnlock(val remaining: Int) : CloudRunResult

    /** Аккаунт или сервер сменились: дальнейшие запросы не выполняются. */
    data object ScopeChanged : CloudRunResult

    /** Системное одобрение не зафиксировано: облако не трогаем. */
    data object NotApproved : CloudRunResult

    /** Операции больше нет (уже завершена или удалена). */
    data object NotFound : CloudRunResult
}

/**
 * Durable-выполнение удаления фото устройства. Порядок: операция записывается ДО системного диалога
 * ([create]), одобрение записывается до любых облачных вызовов ([approve]), затем [runCloud] удаляет
 * только подтверждённые ID. Прогресс сохраняется после каждого шага; прерывание (отмена корутины,
 * смена аккаунта, блокировка Vault) оставляет операцию с оставшимися ID.
 *
 * Выполнение не расширяет список целей: свежий поиск лишь сверяет подтверждённые ID — отсутствующие
 * или уже в корзине считаются выполненными, новые копии только учитываются.
 */
internal class DeviceDeleteExecutor(
    private val cloud: DeleteCloudPort,
    private val device: DeviceCopyPort,
    private val vault: VaultPort,
    private val store: DeviceDeleteOperationStore,
    private val currentScope: () -> CacheScope?,
    /** Разблокировка Vault действует только в памяти текущей сессии экрана; не сохраняется. */
    private val hiddenUnlocked: () -> Boolean,
) {
    fun pendingFor(scope: CacheScope): List<DeleteOperation> = store.all().filter { it.scope == scope }

    /** Фиксирует операцию до системного диалога. [plan] уже содержит подтверждённые облачные ID. */
    fun create(
        scope: CacheScope,
        plan: DeviceDeletePlan,
        hashes: Map<String, String>,
        vaultIds: Set<String>,
    ): DeleteOperation {
        val photos = plan.cloudCopies.map { (mediaKey, ids) ->
            OpPhoto(
                mediaKey = mediaKey,
                hash = requireNotNull(hashes[mediaKey]) { "hash missing for $mediaKey" },
                confirmedIds = ids,
                hiddenIds = ids.filter { it in vaultIds },
                doneIds = emptyList(),
            )
        }
        val operation = DeleteOperation(
            id = UUID.randomUUID().toString(),
            scope = scope,
            mode = plan.mode,
            phase = DeleteOpPhase.AWAITING_SYSTEM,
            devicePhotos = plan.devicePhotos,
            photos = photos,
        )
        store.put(operation)
        return operation
    }

    /** Системный диалог отказан: операция снимается, облако не тронуто. */
    fun deny(opId: String): Boolean {
        val op = store.get(opId) ?: return false
        if (op.phase != DeleteOpPhase.AWAITING_SYSTEM || currentScope() != op.scope) return false
        store.remove(opId)
        return true
    }

    /**
     * Системное одобрение. Фиксируется до отмены загрузок: если отмена упадёт, одобрение уже сохранено
     * и операция продолжится, а не потеряется.
     */
    suspend fun approve(opId: String): DeleteOperation? {
        val op = store.get(opId) ?: return null
        if (currentScope() != op.scope) return null
        if (op.phase == DeleteOpPhase.APPROVED) return op
        val approved = op.copy(phase = DeleteOpPhase.APPROVED)
        store.put(approved)
        for (mediaKey in approved.devicePhotos) {
            device.cancelLiveJobs(mediaKey)
            device.forget(mediaKey)
        }
        return approved
    }

    /**
     * Удаляет подтверждённые облачные ID. Перед каждым запросом проверяются область и разблокировка;
     * прогресс сохраняется. [allowHidden] — вызывающий подтверждает, что текущая сессия разблокирована.
     */
    suspend fun runCloud(opId: String, allowHidden: Boolean): CloudRunResult {
        var deleted = 0
        var reconciled = 0
        var newCopies = 0
        var failed = 0
        val initial = store.get(opId) ?: return CloudRunResult.NotFound
        if (initial.phase != DeleteOpPhase.APPROVED) return CloudRunResult.NotApproved
        if (currentScope() != initial.scope) return CloudRunResult.ScopeChanged

        for (mediaKey in initial.photos.map { it.mediaKey }) {
            val op = store.get(opId) ?: return CloudRunResult.NotFound
            val photo = op.photos.first { it.mediaKey == mediaKey }
            if (photo.remainingIds.isEmpty()) continue
            if (currentScope() != op.scope) return CloudRunResult.ScopeChanged

            val state = cloud.copyState(photo.hash)
            if (state == null) {
                failed++
                continue
            }
            val liveIds = (state as? CloudCopy.Present)?.fileIds.orEmpty()
            newCopies += liveIds.count { it !in photo.confirmedIds }
            for (fileId in photo.remainingIds) {
                if (fileId !in liveIds) {
                    // Уже отсутствует или в корзине: цель выполнена, Vault-запись очищается.
                    markDone(opId, mediaKey, fileId)
                    vault.remove(fileId)
                    reconciled++
                    continue
                }
                val hidden = fileId in vault.fileIds() || fileId in photo.hiddenIds
                if (hidden && !(allowHidden && hiddenUnlocked())) {
                    return CloudRunResult.NeedsUnlock(remainingCount(opId))
                }
                if (currentScope() != op.scope) return CloudRunResult.ScopeChanged
                try {
                    cloud.deleteFile(fileId)
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Exception) {
                    failed++
                    continue
                }
                markDone(opId, mediaKey, fileId)
                vault.remove(fileId) // только после подтверждённого удаления
                deleted++
            }
        }
        val remaining = remainingCount(opId)
        if (remaining == 0) store.remove(opId)
        return CloudRunResult.Finished(deleted, reconciled, newCopies, failed, remaining)
    }

    private fun markDone(opId: String, mediaKey: String, fileId: String) {
        val op = store.get(opId) ?: return
        val updated = op.copy(
            photos = op.photos.map { photo ->
                if (photo.mediaKey == mediaKey) photo.copy(doneIds = photo.doneIds + fileId) else photo
            },
        )
        store.put(updated)
    }

    private fun remainingCount(opId: String): Int = store.get(opId)?.remainingCount ?: 0
}
