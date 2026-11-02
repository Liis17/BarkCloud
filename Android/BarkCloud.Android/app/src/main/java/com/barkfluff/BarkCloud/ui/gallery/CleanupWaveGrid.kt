package com.barkfluff.BarkCloud.ui.gallery

import android.animation.ValueAnimator
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.AnimationVector1D
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.layer.drawLayer
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.localPreviewModel
import kotlin.math.ceil
import kotlin.math.roundToInt

// Разлёт 9 частиц плитки (dp), как p1..p9 в макете «C · Волна».
private val particleDx = floatArrayOf(-26f, 8f, 34f, -40f, 12f, 44f, -18f, -6f, 22f)
private val particleDy = floatArrayOf(-260f, -300f, -230f, -290f, -250f, -310f, -220f, -280f, -240f)
private val cssEaseOut = CubicBezierEasing(0f, 0f, 0.58f, 1f)

/**
 * Неинтерактивная сетка 3×N под шапкой экрана очистки. При [running] сетка едет вверх с ускорением,
 * а каждый ряд у верхнего края рассыпается на частицы. Кадры читают [elapsed] только в фазе
 * отрисовки, без рекомпозиций.
 */
@Composable
internal fun CleanupWaveGrid(
    items: List<DeviceMedia>,
    running: Boolean,
    elapsed: Animatable<Float, AnimationVector1D>,
    onFinished: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val finished by rememberUpdatedState(onFinished)
    BoxWithConstraints(modifier.clipToBounds()) {
        val pitch = (maxWidth - 8.dp) / 3 + 2.dp
        // Заполняем видимую область и ещё два ряда под нижней панелью, как в макете.
        val rows = minOf((items.size + 2) / 3, ceil(maxHeight / pitch).toInt() + 2)
        val distancePx = with(LocalDensity.current) { (pitch * rows).toPx() }
        LaunchedEffect(running) {
            elapsed.snapTo(0f)
            if (!running) return@LaunchedEffect
            if (ValueAnimator.areAnimatorsEnabled()) {
                val total = CleanupWave.totalSeconds(rows)
                elapsed.animateTo(total, tween((total * 1000).roundToInt(), easing = LinearEasing))
            }
            finished()
        }
        Column(
            Modifier
                .fillMaxWidth()
                .wrapContentHeight(Alignment.Top, unbounded = true)
                .graphicsLayer {
                    translationY = if (running) -CleanupWave.scrollFraction(elapsed.value) * distancePx else 0f
                }
                .padding(horizontal = 2.dp),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            items.take(rows * 3).chunked(3).forEachIndexed { row, cells ->
                val start = CleanupWave.rowStart(row, rows)
                val flight = CleanupWave.rowFlight(row, rows)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                    cells.forEach { item ->
                        key(item.mediaKey) {
                            WaveTile(item, running, start, flight, { elapsed.value }, Modifier.weight(1f).aspectRatio(1f))
                        }
                    }
                    repeat(3 - cells.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
    }
}

@Composable
private fun WaveTile(
    item: DeviceMedia,
    running: Boolean,
    start: Float,
    flight: Float,
    elapsed: () -> Float,
    modifier: Modifier,
) {
    val layer = rememberGraphicsLayer()
    var snapshot by remember { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(running) {
        // Снимок фиксирует последнее показанное превью: файл к этому моменту уже удалён системой.
        snapshot = if (running && layer.size.width > 0 && layer.size.height > 0) layer.toImageBitmap() else null
    }
    MediaThumb(
        model = item.localPreviewModel(512),
        isVideo = item.isVideo,
        contentDescription = null,
        modifier = modifier.drawWithContent {
            if (!running) layer.record { this@drawWithContent.drawContent() }
            val progress = if (running) (elapsed() - start) / flight else -1f
            val bitmap = snapshot
            when {
                progress < 0f -> drawLayer(layer)
                bitmap == null -> if (progress < 1f) drawLayer(layer)
                progress < 1f -> drawParticles(bitmap, progress)
            }
        }.clip(RoundedCornerShape(4.dp)),
    )
}

/** Плитка исчезает, а 9 фрагментов (30% со смещением 35%) улетают вверх, уменьшаясь и затухая. */
private fun DrawScope.drawParticles(bitmap: ImageBitmap, progress: Float) {
    val move = cssEaseOut.transform(progress)
    val alpha = if (progress < 0.7f) 1f else 1f - cssEaseOut.transform((progress - 0.7f) / 0.3f)
    val scale = 1f - 0.6f * move
    val partWidth = size.width * 0.3f
    val partHeight = size.height * 0.3f
    val dstSize = IntSize(
        (partWidth * scale).roundToInt().coerceAtLeast(1),
        (partHeight * scale).roundToInt().coerceAtLeast(1),
    )
    for (particle in 0 until 9) {
        val col = particle % 3
        val row = particle / 3
        val srcX = (col * 0.35f * bitmap.width).roundToInt()
        val srcY = (row * 0.35f * bitmap.height).roundToInt()
        val srcSize = IntSize(
            (bitmap.width * 0.3f).roundToInt().coerceIn(1, (bitmap.width - srcX).coerceAtLeast(1)),
            (bitmap.height * 0.3f).roundToInt().coerceIn(1, (bitmap.height - srcY).coerceAtLeast(1)),
        )
        // Масштаб — относительно центра частицы, как transform-origin в CSS.
        val left = col * 0.35f * size.width + particleDx[particle].dp.toPx() * move + partWidth * (1f - scale) / 2f
        val top = row * 0.35f * size.height + particleDy[particle].dp.toPx() * move + partHeight * (1f - scale) / 2f
        drawImage(
            image = bitmap,
            srcOffset = IntOffset(srcX, srcY),
            srcSize = srcSize,
            dstOffset = IntOffset(left.roundToInt(), top.roundToInt()),
            dstSize = dstSize,
            alpha = alpha,
        )
    }
}
