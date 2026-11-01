package com.barkfluff.BarkCloud.data.cloud

import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/**
 * Сигнал «облачная библиотека изменилась» (загрузка завершена, файлы удалены). Экраны облака
 * перечитывают свои страницы; состояние присутствия передаётся через Room (media_cloud_states).
 */
object CloudLibraryChanges {
    private val _events = MutableSharedFlow<Unit>(extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)
    val events: SharedFlow<Unit> = _events.asSharedFlow()

    fun notifyChanged() {
        _events.tryEmit(Unit)
    }
}
