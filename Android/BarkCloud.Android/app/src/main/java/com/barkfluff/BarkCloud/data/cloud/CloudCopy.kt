package com.barkfluff.BarkCloud.data.cloud

/** Лимит сервера на количество хешей в одном `CheckFileHashes`. */
const val CHECK_HASHES_BATCH = 500

/**
 * Ответ сервера по одному SHA-256. [exists] — есть ли у владельца живая запись с этим контентом
 * (семантика прежнего bool). [fileIds] — пригодные для действий READY-файлы владельца, по возрастанию;
 * пусто при exists=true означает «присутствие есть, но действовать по хешу нельзя» (старый сервер
 * или файл не READY/не принадлежит пользователю в Uploaders).
 */
data class HashPresence(val exists: Boolean, val fileIds: List<String>)

/**
 * Состояние облачной копии файла устройства для принятия решений. [Unknown] — нет ответа сервера
 * либо присутствие без пригодных ID: нельзя ни считать копию отсутствующей, ни выполнять
 * деструктивное действие и ни загружать повторно.
 */
sealed interface CloudCopy {
    /** Копия есть; [fileId] — детерминированно выбранный ID (наименьший из пригодных). */
    data class Present(val fileId: String, val fileIds: List<String>) : CloudCopy

    data object Absent : CloudCopy

    data object Unknown : CloudCopy

    companion object {
        fun of(presence: HashPresence?): CloudCopy = when {
            presence == null -> Unknown
            !presence.exists -> Absent
            presence.fileIds.isEmpty() -> Unknown
            else -> presence.fileIds.sorted().let { sorted -> Present(sorted.first(), sorted) }
        }
    }
}

/**
 * Режет хеши на пакеты по [batch] и собирает ответы. Хеш, которого нет в ответе сервера, в
 * результате отсутствует (UNKNOWN): запрос с пустым ответом не превращается в «нет в облаке».
 */
internal suspend fun checkPresenceBatched(
    hashes: List<String>,
    batch: Int = CHECK_HASHES_BATCH,
    query: suspend (List<String>) -> Map<String, HashPresence>,
): Map<String, HashPresence> {
    val unique = hashes.map { it.lowercase() }.distinct()
    if (unique.isEmpty()) return emptyMap()
    val result = HashMap<String, HashPresence>(unique.size)
    unique.chunked(batch).forEach { chunk -> result.putAll(query(chunk)) }
    return result
}
