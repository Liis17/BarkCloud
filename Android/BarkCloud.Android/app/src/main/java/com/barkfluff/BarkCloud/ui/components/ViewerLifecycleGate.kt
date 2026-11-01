package com.barkfluff.BarkCloud.ui.components

import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.LifecycleOwner

/**
 * Шлюз старта просмотрщика.
 *
 * [started] отражает видимость по [Lifecycle] и инициализируется из текущего состояния, а не значением по умолчанию:
 * просмотр, созданный в CREATED, не может стартовать до ON_START.
 *
 * [autoplayAllowed] — отдельный латч «не резюмировать самостоятельно»: выключается только реальным ON_STOP.
 * Поэтому первый переход CREATED→STARTED без фона может автозапустить видео, а возврат после ON_STOP — нет,
 * пока пользователь явно не нажмёт Play ([onUserAction]).
 */
internal class ViewerPlaybackGate(started: Boolean = true) {
    var started: Boolean = started
        private set

    var autoplayAllowed: Boolean = true
        private set

    fun onStart() {
        started = true
    }

    fun onStop() {
        started = false
        autoplayAllowed = false
    }

    fun onUserAction() {
        autoplayAllowed = true
    }
}

/** Гейт, инициализированный по реальному состоянию владельца жизненного цикла. */
internal fun viewerPlaybackGateFor(lifecycle: Lifecycle): ViewerPlaybackGate =
    ViewerPlaybackGate(started = lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED))

/**
 * Синхронно переводит шлюз на ON_START/ON_STOP. На ON_STOP вызывает [onStopped] (остановка плеера и
 * отмена ожидающего источника), на любое изменение — [onChanged] (ключ эффекта автозапуска в composable).
 */
internal class ViewerLifecycleObserver(
    private val gate: ViewerPlaybackGate,
    private val onStopped: () -> Unit,
    private val onChanged: () -> Unit,
) : LifecycleEventObserver {
    override fun onStateChanged(source: LifecycleOwner, event: Lifecycle.Event) {
        when (event) {
            Lifecycle.Event.ON_START -> gate.onStart()
            Lifecycle.Event.ON_STOP -> {
                gate.onStop()
                onStopped()
            }
            else -> return
        }
        onChanged()
    }
}
