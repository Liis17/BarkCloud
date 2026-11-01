package com.barkfluff.BarkCloud.data.cloud

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/** Что изменилось в облачной библиотеке: для перечитывания страниц и сброса присутствия. */
sealed interface CloudLibraryChange {
    /** Загрузка завершена или дедуплицирована: страницы облака нужно перечитать. */
    data object Changed : CloudLibraryChange

    /** Файл удалён из облака (в корзину): device-состояния, ссылавшиеся на него, больше не валидны. */
    data class FileDeleted(val fileId: String) : CloudLibraryChange
}

/**
 * Межэкранный сигнал о библиотеке. Состояние присутствия передаётся через Room (media_cloud_states);
 * этот поток лишь говорит экранам облака перечитаться.
 */
object CloudLibraryChanges {
    private val _events = MutableSharedFlow<CloudLibraryChange>(extraBufferCapacity = 64)
    val events: SharedFlow<CloudLibraryChange> = _events.asSharedFlow()

    suspend fun publish(change: CloudLibraryChange) {
        _events.emit(change)
    }
}
