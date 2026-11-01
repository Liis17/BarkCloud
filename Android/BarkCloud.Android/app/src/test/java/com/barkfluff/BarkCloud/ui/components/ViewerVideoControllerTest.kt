package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cloud.PlaybackSource
import com.barkfluff.BarkCloud.data.cloud.ResolvedPlayback
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewerVideoControllerTest {
    private val log = mutableListOf<String>()
    private var resolveCount = 0

    private inner class FakeEngine : VideoPlaybackEngine {
        override var positionMs: Long = 0L
        override var durationMs: Long = 0L
        override var isPlaying: Boolean = false
        override var onEvent: (VideoEngineEvent) -> Unit = {}

        override fun load(source: PlaybackSource, startPositionMs: Long, playWhenReady: Boolean) {
            log += "load:${(source as PlaybackSource.Remote).url}@$startPositionMs:$playWhenReady"
        }

        override fun play() { log += "play" }
        override fun pause() { log += "pause" }
        override fun seekTo(positionMs: Long) { log += "seek:$positionMs" }
        override fun setMuted(muted: Boolean) { log += "muted:$muted" }
        override fun stop() { log += "stop" }
        override fun release() { log += "release" }

        fun emit(event: VideoEngineEvent) = onEvent(event)
    }

    /** Источник по ID: каждое разрешение даёт новую «ссылку», снятие пина пишется в лог. */
    private fun resolver(gate: CompletableDeferred<Unit>? = null): suspend (String) -> ResolvedPlayback = { id ->
        log += "resolve:$id"
        gate?.await()
        val url = "https://signed.example/$id/${++resolveCount}"
        ResolvedPlayback(PlaybackSource.Remote(url)) { log += "unpin:$id" }
    }

    private fun TestScope.controller(engine: FakeEngine, gate: CompletableDeferred<Unit>? = null) =
        ViewerVideoController(engine, scope = this, releaseScope = this, resolve = resolver(gate))

    @Test
    fun sameIdDoesNotReloadTheSource() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)

        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        assertEquals(listOf("resolve:a", "load:https://signed.example/a/1@0:true"), log)

        controller.open("a", playWhenReady = true, userInitiated = true)
        advanceUntilIdle()
        assertEquals(listOf("resolve:a", "load:https://signed.example/a/1@0:true", "play"), log)
    }

    @Test
    fun initialNotFoundWhilePlayingReloadsWithTheRequestedPlayback() = runTest {
        // Автозапуск запрошен, но Playing ещё не пришёл: 404 не должен отменять намерение.
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()

        assertEquals(listOf("resolve:a", "load:https://signed.example/a/2@0:true", "unpin:a"), log)
    }

    @Test
    fun buffering404KeepsTheRequestedPlaybackAndPosition() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        engine.emit(VideoEngineEvent.Ready)
        engine.emit(VideoEngineEvent.Playing(true))
        engine.emit(VideoEngineEvent.Buffering(true))
        engine.positionMs = 7_000L
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()

        assertEquals(listOf("resolve:a", "load:https://signed.example/a/2@7000:true", "unpin:a"), log)
    }

    @Test
    fun explicitPauseSurvivesTheRefreshAndKeepsThePosition() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        engine.emit(VideoEngineEvent.Ready)
        engine.emit(VideoEngineEvent.Playing(true))
        controller.togglePlayback()
        engine.emit(VideoEngineEvent.Playing(false))
        engine.positionMs = 9_000L
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()

        assertEquals(listOf("resolve:a", "load:https://signed.example/a/2@9000:false", "unpin:a"), log)
        assertEquals(VideoStatus.READY, controller.state.value.status)
    }

    @Test
    fun backgroundDuringSettleAndResolveNeverStartsThePlayer() = runTest {
        val engine = FakeEngine()
        val gate = CompletableDeferred<Unit>()
        val controller = controller(engine, gate)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()

        controller.onBackground()
        gate.complete(Unit)
        advanceUntilIdle()

        assertTrue(log.none { it.startsWith("load:") || it == "play" })
        assertEquals(VideoStatus.IDLE, controller.state.value.status)
    }

    @Test
    fun afterReturningAutoplayIsIgnoredUntilTheUserPlays() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        controller.onBackground()
        controller.onForeground()
        advanceUntilIdle()
        log.clear()

        controller.open("b", playWhenReady = true)
        advanceUntilIdle()
        assertTrue(log.isEmpty())

        controller.open("b", playWhenReady = true, userInitiated = true)
        advanceUntilIdle()
        assertEquals(listOf("resolve:b", "load:https://signed.example/b/2@0:true"), log)
    }

    @Test
    fun controlsDoNothingWhileBackgroundedAndWorkAgainAfterForeground() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true, userInitiated = true)
        advanceUntilIdle()
        controller.onBackground()
        advanceUntilIdle()
        log.clear()

        controller.togglePlayback()
        controller.retry("a")
        advanceUntilIdle()
        assertTrue(log.isEmpty())

        controller.onForeground()
        controller.retry("a")
        advanceUntilIdle()
        assertTrue(log.contains("resolve:a"))
    }

    @Test
    fun switchingReleasesThePlayerBeforeUnpinningThePreviousSource() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        log.clear()

        controller.open("b", playWhenReady = true)
        advanceUntilIdle()

        assertEquals(
            listOf(
                "stop",
                "unpin:a",
                "resolve:b",
                "load:https://signed.example/b/2@0:true",
            ),
            log,
        )
    }

    @Test
    fun stopReleasesImmediatelyAndCancelsAPendingLoad() = runTest {
        val engine = FakeEngine()
        val gate = CompletableDeferred<Unit>()
        val controller = controller(engine, gate)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()

        controller.stop()
        gate.complete(Unit)
        advanceUntilIdle()

        assertEquals(listOf("resolve:a", "stop"), log)
        assertEquals(VideoStatus.IDLE, controller.state.value.status)
    }

    @Test
    fun notFoundRefreshesOnceAndKeepsThePosition() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        engine.emit(VideoEngineEvent.Ready)
        engine.emit(VideoEngineEvent.Playing(true))
        engine.positionMs = 42_000L
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()

        assertEquals(
            listOf("resolve:a", "load:https://signed.example/a/2@42000:true", "unpin:a"),
            log,
        )
        assertEquals(VideoStatus.READY, controller.state.value.status)
    }

    @Test
    fun secondNotFoundAfterRefreshIsAnInternalError() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.NOT_FOUND))
        advanceUntilIdle()

        assertEquals(VideoStatus.ERROR, controller.state.value.status)
        assertTrue(log.none { it.startsWith("resolve:") || it.startsWith("load:") })
    }

    @Test
    fun otherFailureIsAnErrorWithoutRefresh() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        log.clear()

        engine.emit(VideoEngineEvent.Failed(VideoFailure.OTHER))
        advanceUntilIdle()

        assertEquals(VideoStatus.ERROR, controller.state.value.status)
        assertTrue(log.isEmpty())
    }

    @Test
    fun playAfterEndedRestartsFromTheBeginning() = runTest {
        val engine = FakeEngine()
        val controller = controller(engine)
        controller.open("a", playWhenReady = true)
        advanceUntilIdle()
        engine.emit(VideoEngineEvent.Ended)
        log.clear()

        controller.togglePlayback()

        assertEquals(listOf("seek:0", "play"), log)
    }
}
