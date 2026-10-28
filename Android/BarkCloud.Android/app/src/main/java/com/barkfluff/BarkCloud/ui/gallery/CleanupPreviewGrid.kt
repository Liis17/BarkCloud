package com.barkfluff.BarkCloud.ui.gallery

import android.animation.ValueAnimator
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.layer.drawLayer
import androidx.compose.ui.graphics.rememberGraphicsLayer
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import kotlin.math.roundToInt

/** A bounded, noninteractive preview; particle frames only invalidate drawing. */
@Composable
internal fun CleanupPreviewGrid(items: List<DeviceMedia>, dissolving: Boolean, onFinished: () -> Unit) {
    val progress = remember { Animatable(0f) }
    val finished by rememberUpdatedState(onFinished)
    LaunchedEffect(dissolving) {
        progress.snapTo(0f)
        if (dissolving) {
            if (ValueAnimator.areAnimatorsEnabled()) progress.animateTo(1f, tween(1800, easing = LinearEasing))
            finished()
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items.take(12).chunked(3).forEachIndexed { rowIndex, row ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEachIndexed { column, item ->
                    key(item.mediaKey) {
                        CleanupParticleTile(
                            item, dissolving, { progress.value }, rowIndex * 3 + column,
                            Modifier.weight(1f).aspectRatio(1f),
                        )
                    }
                }
                repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

@Composable
private fun CleanupParticleTile(item: DeviceMedia, dissolving: Boolean, progress: () -> Float, index: Int, modifier: Modifier) {
    val layer = rememberGraphicsLayer()
    var snapshot by remember { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(dissolving) {
        snapshot = if (dissolving) layer.toImageBitmap() else null
    }
    MediaThumb(
        model = item.uri,
        isVideo = item.isVideo,
        contentDescription = null,
        modifier = modifier.drawWithContent {
            if (!dissolving) layer.record { this@drawWithContent.drawContent() }
            val bitmap = snapshot
            if (!dissolving || bitmap == null) {
                drawLayer(layer)
            } else {
                // Tile order compresses over time; each fragment accelerates quadratically upward.
                for (particle in 0 until 36) {
                    val col = particle % 6
                    val row = particle / 6
                    val seed = ((particle * 37 + index * 19) % 101) / 100f
                    val t = (progress() * 1.8f - index * 0.045f - seed * 0.12f).coerceIn(0f, 1f)
                    val sourceX = col * bitmap.width / 6
                    val sourceY = row * bitmap.height / 6
                    val sourceSize = IntSize((col + 1) * bitmap.width / 6 - sourceX, (row + 1) * bitmap.height / 6 - sourceY)
                    // Temporal samples form a directional motion-blur trail, including on API 30.
                    for (sample in 2 downTo 0) {
                        val time = (t - sample * 0.018f).coerceAtLeast(0f)
                        val dx = (seed - 0.5f) * size.width * time * 1.7f
                        val dy = -size.height * (0.35f * time + 12f * time * time)
                        drawImage(
                            image = bitmap,
                            srcOffset = IntOffset(sourceX, sourceY), srcSize = sourceSize,
                            dstOffset = IntOffset((col * size.width / 6 + dx).roundToInt(), (row * size.height / 6 + dy).roundToInt()),
                            dstSize = IntSize((size.width / 6).roundToInt().coerceAtLeast(1), (size.height / 6).roundToInt().coerceAtLeast(1)),
                            alpha = (1f - t) * if (sample == 0) 1f else 0.16f * t,
                        )
                    }
                }
            }
        }.clip(RoundedCornerShape(12.dp)),
    )
}
