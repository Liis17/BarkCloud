package com.barkfluff.BarkCloud.data.upload

/** Чтение состояния задачи перед дедупликацией: задача активна (с текущим альбомом) или уже неактивна. */
sealed interface DedupRead {
    /** Задача ещё ждёт завершения; [albumId] — текущий ожидающий альбом. */
    data class Live(val albumId: String?) : DedupRead

    /** Задача отменена, приостановлена, завершена или удалена: ничего не делаем. */
    data object Inactive : DedupRead
}

/** Результат условного завершения: альбом не менялся ([Done]), задача стала неактивной, или альбом изменился ([Retry]). */
enum class DedupCas { Done, Inactive, Retry }

sealed interface DedupOutcome {
    /** Задача завершена без загрузки; [albumAdded] — был ли добавлен альбом. */
    data class Completed(val albumAdded: Boolean) : DedupOutcome

    /** Задача неактивна: в облако ничего не публикуем, статус не меняем. */
    data object Inactive : DedupOutcome

    /** Альбом есть, но пригодного ID облачной копии нет; повтор возможен. */
    data object MissingFileId : DedupOutcome

    /** Альбом менялся слишком часто; вызывающий получит ошибку и повторит позже. */
    data object Exhausted : DedupOutcome
}

/**
 * Завершение дедупликации с ожидающим альбомом. Цикл ограничен [maxAttempts]: при смене альбома
 * добавление повторяется для нового альбома, при неактивной задаче выход без запросов к альбомам.
 */
internal suspend fun completeDeduplicatedWithAlbum(
    fileId: String?,
    read: suspend () -> DedupRead,
    addToAlbum: suspend (albumId: String, fileId: String) -> Unit,
    completeIfAlbum: suspend (expectedAlbumId: String?) -> DedupCas,
    maxAttempts: Int = MAX_DEDUP_ATTEMPTS,
): DedupOutcome {
    var albumAdded = false
    repeat(maxAttempts) {
        val state = read()
        if (state !is DedupRead.Live) return DedupOutcome.Inactive
        val album = state.albumId
        if (album != null) {
            if (fileId == null) return DedupOutcome.MissingFileId
            addToAlbum(album, fileId)
            albumAdded = true
        }
        when (completeIfAlbum(album)) {
            DedupCas.Done -> return DedupOutcome.Completed(albumAdded)
            DedupCas.Inactive -> return DedupOutcome.Inactive
            DedupCas.Retry -> Unit
        }
    }
    return DedupOutcome.Exhausted
}

internal const val MAX_DEDUP_ATTEMPTS = 5
