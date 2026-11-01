package com.barkfluff.BarkCloud.ui.selection

import kotlinx.coroutines.CancellationException

/**
 * Итог пакетной операции: успешные (их можно снять с выбора), число упавших и нерассмотренные
 * элементы, если пакет остановлен [shouldContinue] (например, блокировка сессии).
 */
internal data class BatchOutcome(
    val succeeded: Set<String>,
    val failedIds: Set<String>,
    val remaining: Set<String> = emptySet(),
) {
    val failed: Int get() = failedIds.size
    val retryIds: Set<String> get() = failedIds + remaining
    val total: Int get() = succeeded.size + retryIds.size
}

/**
 * Последовательно выполняет [action] для каждого id. Перед каждым элементом проверяется
 * [shouldContinue]: при `false` пакет останавливается, оставшиеся ID возвращаются в [BatchOutcome.remaining].
 * Ошибка одного элемента не прерывает пакет и сохраняет его ID в [BatchOutcome.failedIds]; отмена
 * корутины пробрасывается без проглатывания.
 */
internal suspend fun runCloudBatch(
    ids: Collection<String>,
    shouldContinue: () -> Boolean = { true },
    action: suspend (String) -> Unit,
): BatchOutcome {
    val succeeded = LinkedHashSet<String>()
    val failedIds = LinkedHashSet<String>()
    val queue = ids.toList()
    for ((index, id) in queue.withIndex()) {
        if (!shouldContinue()) {
            return BatchOutcome(succeeded, failedIds, remaining = queue.drop(index).toSet())
        }
        try {
            action(id)
            succeeded += id
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            failedIds += id
        }
    }
    return BatchOutcome(succeeded, failedIds)
}
