package com.barkfluff.BarkCloud.ui.components

import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import com.barkfluff.BarkCloud.data.cloud.PlaybackSource
import com.barkfluff.BarkCloud.data.cloud.ResolvedPlayback
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Интеграция шлюза с настоящим LifecycleRegistry. Повторяет связку composable:
 * гейт из текущего состояния владельца, ViewerLifecycleObserver, ключ эффекта автозапуска.
 */
class ViewerLifecycleGateTest {
    private class TestOwner : LifecycleOwner {
        private val registry = LifecycleRegistry.createUnsafe(this)
        override val lifecycle: Lifecycle get() = registry
        fun event(event: Lifecycle.Event) = registry.handleLifecycleEvent(event)
    }

    private class RecordingEngine(private val log: MutableList<String>) : VideoPlaybackEngine {
        override var positionMs: Long = 0L
        override var durationMs: Long = 0L
        override var isPlaying: Boolean = false
        override var onEvent: (VideoEngineEvent) -> Unit = {}
        override fun load(source: PlaybackSource, startPositionMs: Long, playWhenReady: Boolean) {
            log += "load:$playWhenReady"
        }
        override fun play() { log += "play" }
        override fun pause() { log += "pause" }
        override fun seekTo(positionMs: Long) { log += "seek" }
        override fun setMuted(muted: Boolean) {}
        override fun stop() { log += "stop" }
        override fun release() { log += "release" }
    }

    /** То, что делает composable: гейт, наблюдатель владельца и эффект автозапуска. */
    private class Host(owner: TestOwner, scope: TestScope, pendingResolve: CompletableDeferred<Unit>? = null) {
        val log = mutableListOf<String>()
        val gate = viewerPlaybackGateFor(owner.lifecycle)
        val controller = ViewerVideoController(RecordingEngine(log), scope, scope, gate) { _ ->
            pendingResolve?.await()
            log += "resolve"
            ResolvedPlayback(PlaybackSource.Remote("https://x.example/v")) {}
        }
        private val observer = ViewerLifecycleObserver(
            gate = gate,
            onStopped = { controller.stop() },
            onChanged = {},
        )

        init {
            owner.lifecycle.addObserver(observer)
        }

        /** Эффект автозапуска, перезапускаемый при смене ключа (в том числе lifecycleStarted). */
        fun autoplayEffect() {
            controller.open("a", playWhenReady = true)
        }
    }

    @Test
    fun ownerCreatedWithoutStartNeverAutoplays() = runTest {
        val owner = TestOwner()
        owner.event(Lifecycle.Event.ON_CREATE)
        val host = Host(owner, this)

        host.autoplayEffect()
        advanceUntilIdle()

        assertTrue(host.log.isEmpty())
    }

    @Test
    fun firstForegroundLaunchFromCreatedMayAutoplayWhenNeverBackgrounded() = runTest {
        val owner = TestOwner()
        owner.event(Lifecycle.Event.ON_CREATE)
        val host = Host(owner, this)
        host.autoplayEffect()
        advanceUntilIdle()

        owner.event(Lifecycle.Event.ON_START)
        host.autoplayEffect()
        advanceUntilIdle()

        assertEquals(listOf("resolve", "load:true"), host.log)
    }

    @Test
    fun realStopAndReturnRequiresExplicitPlayAndIgnoresAutoplayEffect() = runTest {
        val owner = TestOwner()
        owner.event(Lifecycle.Event.ON_CREATE)
        owner.event(Lifecycle.Event.ON_START)
        val host = Host(owner, this)
        host.autoplayEffect()
        advanceUntilIdle()
        assertEquals(listOf("resolve", "load:true"), host.log)

        owner.event(Lifecycle.Event.ON_STOP)
        advanceUntilIdle()
        owner.event(Lifecycle.Event.ON_START)
        // Эффект автозапуска перезапускается по ключу lifecycleStarted, но автозапуск после возврата запрещён.
        host.autoplayEffect()
        advanceUntilIdle()
        assertEquals(listOf("resolve", "load:true", "stop"), host.log)

        host.controller.open("a", playWhenReady = true, userInitiated = true)
        advanceUntilIdle()
        assertEquals(listOf("resolve", "load:true", "stop", "resolve", "load:true"), host.log)
    }

    @Test
    fun stopWhileResolveIsPendingNeverStartsPlayback() = runTest {
        val owner = TestOwner()
        owner.event(Lifecycle.Event.ON_CREATE)
        owner.event(Lifecycle.Event.ON_START)
        val gate = CompletableDeferred<Unit>()
        val host = Host(owner, this, pendingResolve = gate)
        host.autoplayEffect()
        advanceUntilIdle()

        owner.event(Lifecycle.Event.ON_STOP)
        gate.complete(Unit)
        advanceUntilIdle()

        assertTrue(host.log.none { it.startsWith("load") || it == "play" })
    }

    @Test
    fun controlsAreIgnoredWhileStoppedAndWorkAfterExplicitReturn() = runTest {
        val owner = TestOwner()
        owner.event(Lifecycle.Event.ON_CREATE)
        owner.event(Lifecycle.Event.ON_START)
        val host = Host(owner, this)
        host.controller.open("a", playWhenReady = true, userInitiated = true)
        advanceUntilIdle()
        owner.event(Lifecycle.Event.ON_STOP)
        advanceUntilIdle()
        host.log.clear()

        host.controller.togglePlayback()
        host.controller.retry("a")
        advanceUntilIdle()
        assertTrue(host.log.isEmpty())

        owner.event(Lifecycle.Event.ON_START)
        host.controller.retry("a")
        advanceUntilIdle()
        assertEquals(listOf("resolve", "load:true"), host.log)
    }
}
