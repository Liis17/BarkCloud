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

internal sealed interface VideoEngineEvent {
    data object Ready : VideoEngineEvent
    data class Buffering(val active: Boolean) : VideoEngineEvent
    data class Playing(val active: Boolean) : VideoEngineEvent
    data object Ended : VideoEngineEvent
    data class Failed(val failure: VideoFailure) : VideoEngineEvent
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
)

/**
 * Один плеер на просмотрщик, привязанный к стабильному ID элемента (а не к индексу страницы).
 * При переключении и [stop] источник снимается немедленно (`engine.stop`), затем снимается пин кеша
 * через [releaseScope], чтобы unpin дошёл до конца даже после ухода композиции. Истёкшая ссылка (404) обновляется один раз
 * с сохранением позиции; повторная ошибка переводит в [VideoStatus.ERROR].
 */
internal class ViewerVideoController(
    private val engine: VideoPlaybackEngine,
    private val scope: CoroutineScope,
    private val releaseScope: CoroutineScope,
    private val resolve: suspend (id: String) -> ResolvedPlayback,
) {
    private val _state = MutableStateFlow(VideoPlaybackState())
    val state: StateFlow<VideoPlaybackState> = _state.asStateFlow()

    private var activeId: String? = null
    private var activeResolved: ResolvedPlayback? = null
    private var job: Job? = null
    private var refreshedForActive = false
    private var lastPositionMs = 0L

    init {
        engine.onEvent = ::onEngineEvent
    }

    /** Открывает элемент. Повторный вызов для того же ID не перезагружает источник. */
    fun open(id: String, playWhenReady: Boolean) {
        if (id == activeId) {
            if (playWhenReady) engine.play()
            return
        }
        closeActive()
        activeId = id
        refreshedForActive = false
        lastPositionMs = 0L
        _state.value = VideoPlaybackState(status = VideoStatus.LOADING, muted = _state.value.muted)
        job = scope.launch {
            val resolved = resolveOrFail(id) ?: return@launch
            if (activeId != id) {
                releaseScope.launch { resolved.release() }
                return@launch
            }
            activeResolved = resolved
            engine.load(resolved.source, startPositionMs = 0L, playWhenReady = playWhenReady)
        }
    }

    /** Немедленная остановка: сначала освобождается плеер, затем снимается пин кеша. */
    fun stop() {
        closeActive()
        _state.value = VideoPlaybackState(muted = _state.value.muted)
    }

    fun retry(id: String) {
        stop()
        open(id, playWhenReady = true)
    }

    fun togglePlayback() {
        val current = _state.value
        when {
            current.playing -> engine.pause()
            current.ended -> { engine.seekTo(0L); engine.play() }
            else -> engine.play()
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
            VideoEngineEvent.Ended -> _state.update { it.copy(playing = false, ended = true) }
            is VideoEngineEvent.Failed -> onFailure(event.failure)
        }
    }

    private fun onFailure(failure: VideoFailure) {
        val id = activeId ?: return
        if (failure == VideoFailure.NOT_FOUND && !refreshedForActive) {
            refreshedForActive = true
            refreshSource(id, positionMs = lastPositionMs, wasPlaying = _state.value.playing)
            return
        }
        _state.update { it.copy(status = VideoStatus.ERROR, playing = false, buffering = false) }
    }

    private fun refreshSource(id: String, positionMs: Long, wasPlaying: Boolean) {
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
            engine.load(fresh.source, startPositionMs = positionMs, playWhenReady = wasPlaying)
        }
    }
}
