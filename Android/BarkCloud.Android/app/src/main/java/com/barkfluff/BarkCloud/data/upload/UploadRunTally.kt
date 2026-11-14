package com.barkfluff.BarkCloud.data.upload

import java.util.concurrent.ConcurrentHashMap

/**
 * Счёт прохода для уведомления. `done` — завершённые после старта прохода, `remaining` —
 * активные eligible задачи, `failed` — ошибки, возникшие в этом проходе и не повторённые.
 * Ошибки, существовавшие до старта, в знаменатель не входят. Счётчики пересчитываются из Room
 * при каждом обновлении, поэтому пауза и отмена выводят задачу из счёта, а новые — увеличивают.
 */
data class UploadRunTally(val done: Int, val remaining: Int, val failed: Int) {
    val total: Int get() = done + remaining + failed
    val percent: Int get() = if (total == 0) 0 else done * 100 / total

    /** Короткий текст чипа: `done/total`, если влезает в [CHIP_MAX_LENGTH] символов, иначе процент. */
    fun chipText(): String = "$done/$total".takeIf { it.length <= CHIP_MAX_LENGTH } ?: "$percent%"

    companion object {
        const val CHIP_MAX_LENGTH = 6
    }
}

/**
 * Состояние одного прохода воркера. Ошибки запоминаются по id задач, завершившихся неуспешно
 * в этом проходе; их текущая фаза читается из Room, поэтому повтор ошибки снимает её с счёта.
 * Состояние не переживает процесс — перезапуск и successor-проход начинают счёт заново.
 */
internal class UploadRunProgress(
    private val queue: UploadQueueStore,
    private val sinceMillis: Long,
    private val includeBackup: () -> Boolean,
) {
    // Задачи обрабатываются параллельно на Dispatchers.Default, поэтому набор потокобезопасный.
    private val failedIds = ConcurrentHashMap.newKeySet<String>()

    suspend fun tally(): UploadRunTally = queue.runTally(sinceMillis, includeBackup(), failedIds.toList())

    suspend fun noteEnded(id: String) {
        val phase = queue.byId(id)?.phase ?: return
        if (phase == UploadPhase.FAILED || phase == UploadPhase.UPLOADED_NOT_ATTACHED) failedIds.add(id)
    }
}
