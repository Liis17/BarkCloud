package com.barkfluff.BarkCloud.ui.selection

import kotlinx.coroutines.CancellationException

/** Итог пакетной операции: какие элементы прошли (их можно снять с выбора) и сколько упало. */
internal data class BatchOutcome(val succeeded: Set<String>, val failed: Int) {
    val total: Int get() = succeeded.size + failed
}

/**
 * Последовательно выполняет [action] для каждого id. Ошибка одного элемента не прерывает пакет и
 * считается в [BatchOutcome.failed]; отмена корутины пробрасывается без проглатывания.
 */
internal suspend fun runCloudBatch(ids: Collection<String>, action: suspend (String) -> Unit): BatchOutcome {
    val succeeded = LinkedHashSet<String>()
    var failed = 0
    for (id in ids) {
        try {
            action(id)
            succeeded += id
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            failed++
        }
    }
    return BatchOutcome(succeeded, failed)
}
