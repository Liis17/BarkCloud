package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cloud.PlaybackSource
import com.barkfluff.BarkCloud.data.cloud.ResolvedPlayback
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

internal enum class VideoFailure { NOT_FOUND, OTHER }

/** Размер кадра из плеера: [pixelRatio] — пиксельное соотношение сторон, [rotationDegrees] — неприменённый поворот. */
internal data class VideoSize(val width: Int, val height: Int, val pixelRatio: Float, val rotationDegrees: Int)

internal sealed interface VideoEngineEvent {
    data object Ready : VideoEngineEvent
    data class Buffering(val active: Boolean) : VideoEngineEvent
    data class Playing(val active: Boolean) : VideoEngineEvent
    data object Ended : VideoEngineEvent
    data class Failed(val failure: VideoFailure) : VideoEngineEvent
    data class SizeChanged(val size: VideoSize) : VideoEngineEvent
}

/** Граница над плеером: Media3 в [ExoVideoPlaybackEngine], в тестах — фейк. */
internal interface VideoPlaybackEngine {
    val positionMs: Long
    val durationMs: Long
    val isPlaying: Boolean
    var onEvent: (VideoEngineEvent) -> Unit
    fun load(source: PlaybackSource, startPositionMs: Long, playWhenReady: Boolean)
    fun play()
    fun pause()
    fun seekTo(positionMs: Long)
    fun setMuted(muted: Boolean)
    /** Останавливает текущий источник и отпускает его файловые и сетевые ресурсы; плеер остаётся пригодным. */
    fun stop()
    /** Терминально освобождает плеер при уходе просмотрщика; после вызова движок не используется. */
    fun release()
}

internal enum class VideoStatus { IDLE, LOADING, READY, ERROR }

internal data class VideoPlaybackState(
    val status: VideoStatus = VideoStatus.IDLE,
    val playing: Boolean = false,
    val buffering: Boolean = false,
    val ended: Boolean = false,
    val muted: Boolean = false,
    val videoSize: VideoSize? = null,
)

/**
 * Один плеер на просмотрщик, привязанный к стабильному ID элемента (а не к индексу страницы).
 *
 * - Намерение воспроизведения ([playbackIntent]) отделено от фактического состояния плеера: после 404 или буферизации
 *   источник перезагружается с тем же намерением, а явная пауза остаётся паузой.
 * - При переключении и [stop] источник снимается немедленно (`engine.stop`), затем пин кеша снимается через
 *   [releaseScope]; плеер освобождается до unpin.
 * - Истёкшая ссылка (404) обновляется один раз с сохранением позиции; повторная ошибка — [VideoStatus.ERROR].
 * - [onBackground] синхронно запрещает любые старты до [onForeground]; после возврата автозапуск выключен,
 *   воспроизведение запускает только действие пользователя ([open] с `userInitiated = true`).
 */
internal class ViewerVideoController(
    private val engine: VideoPlaybackEngine,
    private val scope: CoroutineScope,
    private val releaseScope: CoroutineScope,
    private val gate: ViewerPlaybackGate = ViewerPlaybackGate(),
    private val resolve: suspend (id: String) -> ResolvedPlayback,
) {
    private val _state = MutableStateFlow(VideoPlaybackState())
    val state: StateFlow<VideoPlaybackState> = _state.asStateFlow()

    private var activeId: String? = null
    private var activeResolved: ResolvedPlayback? = null
    private var job: Job? = null
    private var refreshedForActive = false
    private var lastPositionMs = 0L
    private var playbackIntent = false

    init {
        engine.onEvent = ::onEngineEvent
    }

    /**
     * Открывает элемент. Повторный вызов для того же ID не перезагружает источник.
     * Автозапуск ([userInitiated] = false) игнорируется в фоне и после возврата до действия пользователя.
     */
    fun open(id: String, playWhenReady: Boolean, userInitiated: Boolean = false) {
        if (!gate.started) return
        if (!userInitiated && !gate.autoplayAllowed) return
        if (userInitiated) gate.onUserAction()
        if (id == activeId) {
            if (userInitiated) {
                playbackIntent = true
                engine.play()
            }
            return
        }
        closeActive()
        activeId = id
        refreshedForActive = false
        lastPositionMs = 0L
        playbackIntent = playWhenReady
        _state.value = VideoPlaybackState(status = VideoStatus.LOADING, muted = _state.value.muted)
        job = scope.launch {
            val resolved = resolveOrFail(id) ?: return@launch
            if (activeId != id) {
                releaseScope.launch { resolved.release() }
                return@launch
            }
            activeResolved = resolved
            engine.load(resolved.source, startPositionMs = 0L, playWhenReady = playbackIntent)
        }
    }

    /** Немедленная остановка: источник снимается, пин кеша уходит в [releaseScope]. */
    fun stop() {
        closeActive()
        playbackIntent = false
        _state.value = VideoPlaybackState(muted = _state.value.muted)
    }

    /** Синхронно вызывается при уходе приложения в фон: любые старты запрещены до [onForeground]. */
    fun onBackground() {
        gate.onStop()
        stop()
    }

    fun onForeground() {
        gate.onStart()
    }

    fun retry(id: String) {
        stop()
        open(id, playWhenReady = true, userInitiated = true)
    }

    fun togglePlayback() {
        if (!gate.started) return
        gate.onUserAction()
        val current = _state.value
        when {
            current.playing -> {
                playbackIntent = false
                engine.pause()
            }
            current.ended -> {
                playbackIntent = true
                engine.seekTo(0L)
                engine.play()
            }
            else -> {
                playbackIntent = true
                engine.play()
            }
        }
    }

    fun seekTo(positionMs: Long) {
        lastPositionMs = positionMs
        engine.seekTo(positionMs)
    }

    fun setMuted(muted: Boolean) {
        engine.setMuted(muted)
        _state.update { it.copy(muted = muted) }
    }

    /** Запоминает последнюю позицию, чтобы refresh после 404 продолжил с неё. */
    fun samplePosition(): Long {
        lastPositionMs = engine.positionMs
        return lastPositionMs
    }

    private fun closeActive() {
        job?.cancel()
        job = null
        if (activeId != null) engine.stop()
        activeId = null
        val released = activeResolved
        activeResolved = null
        if (released != null) releaseScope.launch { released.release() }
    }

    private suspend fun resolveOrFail(id: String): ResolvedPlayback? = try {
        resolve(id)
    } catch (error: CancellationException) {
        throw error
    } catch (_: Exception) {
        if (activeId == id) _state.update { it.copy(status = VideoStatus.ERROR, playing = false, buffering = false) }
        null
    }

    private fun onEngineEvent(event: VideoEngineEvent) {
        lastPositionMs = engine.positionMs
        when (event) {
            VideoEngineEvent.Ready -> _state.update { it.copy(status = VideoStatus.READY, buffering = false) }
            is VideoEngineEvent.Buffering -> _state.update { it.copy(buffering = event.active) }
            is VideoEngineEvent.Playing -> _state.update { it.copy(playing = event.active, ended = false) }
            VideoEngineEvent.Ended -> {
                playbackIntent = false
                _state.update { it.copy(playing = false, ended = true) }
            }
            is VideoEngineEvent.Failed -> onFailure(event.failure)
            is VideoEngineEvent.SizeChanged -> _state.update { it.copy(videoSize = event.size) }
        }
    }

    private fun onFailure(failure: VideoFailure) {
        val id = activeId ?: return
        if (failure == VideoFailure.NOT_FOUND && !refreshedForActive) {
            refreshedForActive = true
            refreshSource(id, positionMs = lastPositionMs)
            return
        }
        _state.update { it.copy(status = VideoStatus.ERROR, playing = false, buffering = false) }
    }

    private fun refreshSource(id: String, positionMs: Long) {
        job?.cancel()
        job = scope.launch {
            val fresh = resolveOrFail(id) ?: return@launch
            if (activeId != id) {
                releaseScope.launch { fresh.release() }
                return@launch
            }
            val previous = activeResolved
            activeResolved = fresh
            if (previous != null) releaseScope.launch { previous.release() }
            engine.load(fresh.source, startPositionMs = positionMs, playWhenReady = playbackIntent)
        }
    }
}

/**
 * Состояние ползунка перемотки: живая позиция из тика плеера и позиция, которую пользователь тянет.
 * Тик не перезаписывает перетаскивание; при окончании перетаскивания возвращается только целевая позиция.
 */
internal data class VideoSeekState(
    val livePositionMs: Long = 0L,
    val dragPositionMs: Long? = null,
) {
    val displayPositionMs: Long get() = dragPositionMs ?: livePositionMs

    fun withLivePosition(positionMs: Long): VideoSeekState = copy(livePositionMs = positionMs)

    fun dragTo(positionMs: Long): VideoSeekState = copy(dragPositionMs = positionMs)

    /** Целевая позиция для одного финального seek; null, если перетаскивания не было. */
    fun dragTarget(): Long? = dragPositionMs

    /**
     * Завершает перетаскивание: цель становится живой позицией, поэтому на паузе (без тиков)
     * ползунок и подпись остаются на позиции, выбранной пользователем, а не откатываются.
     */
    fun finishDrag(): VideoSeekState {
        val target = dragPositionMs ?: return this
        return copy(livePositionMs = target, dragPositionMs = null)
    }
}
