package com.barkfluff.BarkCloud.ui.components

import android.content.Context
import android.view.TextureView
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.VolumeOff
import androidx.compose.material.icons.automirrored.filled.VolumeUp
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.HttpDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.PlaybackSource
import kotlinx.coroutines.delay
import okhttp3.Call
import java.util.Locale
import java.util.concurrent.TimeUnit

private const val POSITION_TICK_MS = 250L

/**
 * Media3-плеер за [VideoPlaybackEngine]. Звук включается с аудиофокусом; один экземпляр на просмотрщик.
 * Потоковые запросы идут через [streamingCallFactory] без общего таймаута вызова.
 * Ошибка 404 распознаётся по цепочке причин и отдаётся как [VideoFailure.NOT_FOUND].
 */
@OptIn(UnstableApi::class)
internal class ExoVideoPlaybackEngine(
    context: Context,
    streamingCallFactory: Call.Factory,
) : VideoPlaybackEngine {
    private var released = false
    override var onEvent: (VideoEngineEvent) -> Unit = {}

    private val listener = object : Player.Listener {
        override fun onPlaybackStateChanged(playbackState: Int) {
            when (playbackState) {
                Player.STATE_READY -> {
                    onEvent(VideoEngineEvent.Buffering(false))
                    onEvent(VideoEngineEvent.Ready)
                }
                Player.STATE_BUFFERING -> onEvent(VideoEngineEvent.Buffering(true))
                Player.STATE_ENDED -> onEvent(VideoEngineEvent.Ended)
            }
        }

        override fun onIsPlayingChanged(isPlaying: Boolean) {
            onEvent(VideoEngineEvent.Playing(isPlaying))
        }

        override fun onPlayerError(error: PlaybackException) {
            onEvent(VideoEngineEvent.Failed(classify(error)))
        }
    }

    private val player: ExoPlayer = ExoPlayer.Builder(context)
        .setMediaSourceFactory(
            DefaultMediaSourceFactory(
                DefaultDataSource.Factory(context, OkHttpDataSource.Factory(streamingCallFactory)),
            ),
        )
        .setAudioAttributes(
            AudioAttributes.Builder()
                .setContentType(C.AUDIO_CONTENT_TYPE_MOVIE)
                .setUsage(C.USAGE_MEDIA)
                .build(),
            /* handleAudioFocus = */ true,
        )
        .build()
        .also { it.addListener(listener) }

    override val positionMs: Long get() = if (released) 0L else player.currentPosition
    override val durationMs: Long get() = if (released) 0L else player.duration.takeIf { it > 0L } ?: 0L
    override val isPlaying: Boolean get() = !released && player.isPlaying

    fun attach(view: TextureView) {
        if (!released) player.setVideoTextureView(view)
    }

    override fun load(source: PlaybackSource, startPositionMs: Long, playWhenReady: Boolean) {
        if (released) return
        val uri = when (source) {
            is PlaybackSource.Remote -> source.url
            is PlaybackSource.Local -> source.uri
        }
        player.setMediaItem(MediaItem.fromUri(uri), startPositionMs)
        player.playWhenReady = playWhenReady
        player.prepare()
    }

    override fun play() {
        if (!released) player.play()
    }

    override fun pause() {
        if (!released) player.pause()
    }

    override fun seekTo(positionMs: Long) {
        if (!released) player.seekTo(positionMs)
    }

    override fun setMuted(muted: Boolean) {
        if (!released) player.volume = if (muted) 0f else 1f
    }

    override fun stop() {
        if (released) return
        player.stop()
        player.clearMediaItems()
    }

    override fun release() {
        if (released) return
        released = true
        player.removeListener(listener)
        player.release()
    }

    private fun classify(error: PlaybackException): VideoFailure {
        var cause: Throwable? = error
        while (cause != null) {
            if (cause is HttpDataSource.InvalidResponseCodeException && cause.responseCode == 404) {
                return VideoFailure.NOT_FOUND
            }
            cause = cause.cause
        }
        return VideoFailure.OTHER
    }
}

/** Плеер и контроллер одного просмотрщика; создаются при первом видео и освобождаются вместе с ним. */
internal class ViewerVideoSession(
    val engine: ExoVideoPlaybackEngine,
    val controller: ViewerVideoController,
) {
    /** Сначала снимает источник и пин кеша, затем освобождает плеер. */
    fun close() {
        controller.stop()
        engine.release()
    }
}

/**
 * Видео внутри страницы просмотрщика. Тап переключает оверлей (как у фото), drag и zoom общей оболочки
 * работают поверх [TextureView], который не перехватывает касания. Экран не гаснет, пока играет видео.
 */
@Composable
internal fun ViewerVideoPlayer(
    controller: ViewerVideoController,
    engine: ExoVideoPlaybackEngine,
    itemId: String,
    controlsVisible: Boolean,
    onTap: () -> Unit,
    controlsBottomPadding: Dp,
    modifier: Modifier = Modifier,
) {
    val state by controller.state.collectAsStateWithLifecycle()
    var positionMs by remember { mutableLongStateOf(0L) }
    var durationMs by remember { mutableLongStateOf(0L) }

    LaunchedEffect(state.playing) {
        while (true) {
            positionMs = controller.samplePosition()
            durationMs = engine.durationMs
            if (!state.playing) break
            delay(POSITION_TICK_MS)
        }
    }

    Box(modifier.fillMaxSize().clickable(onClick = onTap)) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { context -> TextureView(context).also { engine.attach(it) } },
            update = { view -> view.keepScreenOn = state.playing },
        )

        when (state.status) {
            VideoStatus.LOADING -> CircularProgressIndicator(Modifier.align(Alignment.Center), color = Color.White)
            VideoStatus.ERROR -> Column(Modifier.align(Alignment.Center), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(stringResource(R.string.common_error_generic), color = Color.White)
                Button(
                    onClick = { controller.retry(itemId) },
                    modifier = Modifier.padding(top = 12.dp),
                ) { Text(stringResource(R.string.viewer_retry_original)) }
            }
            else -> if (state.buffering) {
                CircularProgressIndicator(Modifier.align(Alignment.Center), color = Color.White)
            }
        }

        if (!state.playing && state.status != VideoStatus.LOADING && state.status != VideoStatus.ERROR) {
            IconButton(
                onClick = {
                    if (state.status == VideoStatus.IDLE) controller.open(itemId, playWhenReady = true)
                    else controller.togglePlayback()
                },
                modifier = Modifier.align(Alignment.Center).size(72.dp),
            ) {
                Icon(
                    imageVector = Icons.Filled.PlayArrow,
                    contentDescription = stringResource(R.string.viewer_play_video),
                    tint = Color.White,
                    modifier = Modifier.size(56.dp),
                )
            }
        }

        if (controlsVisible && state.status == VideoStatus.READY) {
            Row(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .navigationBarsPadding()
                    .padding(start = 12.dp, end = 12.dp, top = 8.dp, bottom = controlsBottomPadding),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = controller::togglePlayback) {
                    Icon(
                        imageVector = if (state.playing) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                        contentDescription = null,
                        tint = Color.White,
                    )
                }
                Slider(
                    value = positionMs.coerceAtMost(durationMs.coerceAtLeast(1L)).toFloat(),
                    onValueChange = { positionMs = it.toLong() },
                    onValueChangeFinished = { controller.seekTo(positionMs) },
                    valueRange = 0f..durationMs.coerceAtLeast(1L).toFloat(),
                    modifier = Modifier.weight(1f).padding(horizontal = 8.dp),
                )
                Text(
                    text = viewerTimeLabel(positionMs, durationMs),
                    color = Color.White,
                )
                IconButton(onClick = { controller.setMuted(!state.muted) }) {
                    Icon(
                        imageVector = if (state.muted) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                        contentDescription = stringResource(
                            if (state.muted) R.string.viewer_unmute else R.string.viewer_mute,
                        ),
                        tint = Color.White,
                    )
                }
            }
        }
    }
}

/** Подпись времени «m:ss / m:ss»; для часового видео минуты продолжаются, например «75:03». */
internal fun viewerTimeLabel(positionMs: Long, durationMs: Long): String =
    "${formatMediaTime(positionMs)} / ${formatMediaTime(durationMs)}"

internal fun formatMediaTime(ms: Long): String {
    val totalSeconds = TimeUnit.MILLISECONDS.toSeconds(ms.coerceAtLeast(0L))
    return String.format(Locale.ROOT, "%d:%02d", totalSeconds / 60, totalSeconds % 60)
}
